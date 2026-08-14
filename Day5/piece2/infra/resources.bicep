@description('Environment name')
param environmentName string

@description('Location')
param location string

@description('Unique resource token')
param resourceToken string

@description('Resource tags')
param tags object

param quotesApiExists bool = false
param quotesApiImageName string = ''

var abbreviations = loadJsonContent('./abbreviations.json')

// Log Analytics workspace -- backing store for Application Insights below.
//
// A workspace-based Application Insights component does not hold its own
// data: `requests`, `dependencies`, `traces` and the rest are tables in
// THIS workspace, which is what the KQL in docs/day5-appinsights-submission.md
// actually queries. Classic (non-workspace) components were retired, so
// there is no version of this that skips the workspace.
//
// A dedicated workspace rather than the one behind the shared
// `thinkschool-env` Container Apps Environment: that workspace lives in
// `thinkschool-rg` and is owned by the manual-CLI exercise, and it
// collects container *console/system* logs for every app in the
// environment. Application telemetry from this exercise belongs to this
// exercise's resource group, where `azd down` will remove it with
// everything else, and where its query surface is not shared with
// unrelated apps.
//
// PerGB2018 is the only generally-available SKU for new workspaces;
// 30-day retention is the included, no-extra-cost default. Both are
// stated explicitly rather than left to defaults so the cost profile of
// this file is readable without consulting the API version's defaults.
resource logAnalyticsWorkspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${abbreviations.logAnalyticsWorkspace}${resourceToken}'
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

// Application Insights -- the ingestion endpoint QuotesApi's OpenTelemetry
// setup exports to.
//
// QuotesApi/Extensions/ObservabilityExtensions.cs wires UseAzureMonitor()
// ONLY when a connection string is present, and registers the ASP.NET Core
// and HttpClient instrumentation itself only when it is absent (the Azure
// Monitor distro brings its own; registering both would double-count every
// request and corrupt exactly the percentiles this exercise measures). So
// the connection string below is not just configuration -- it is the switch
// that selects which of the two telemetry pipelines the app runs. Nothing
// reaches Azure without it, which is why the deployed app emitted nothing
// before this resource existed.
resource applicationInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${abbreviations.applicationInsights}-quotes-api-${resourceToken}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalyticsWorkspace.id
    IngestionMode: 'LogAnalytics'
  }
}

// Container Registry
resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: '${abbreviations.containerRegistry}${resourceToken}'
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: true
  }
  tags: tags
}

// Container Apps Environment -- an *existing* reference, not a new
// resource. The original draft of this file provisioned a dedicated
// `thinkschool-azd-env`, on the reasoning in docs/azd-deployment.md
// section 1: keep this exercise fully independent of the manual
// az-cli exercise's `thinkschool-env`. That reasoning was correct but
// incomplete -- it did not account for a subscription-level limit,
// found only by actually running `azd up`: this subscription allows
// exactly one Container Apps Environment per region
// (MaxNumberOfRegionalEnvironmentsInSubExceeded), and `thinkschool-env`
// in `thinkschool-rg` already occupies centralindia. A second,
// dedicated environment for this exercise is therefore not available
// at any resource-group boundary; sharing the existing environment is
// not a preference here, it is the only option the subscription
// allows. `azd down` on this workspace still only deletes what it
// created in `thinkschool-azd-rg` -- the registry, identity, role
// assignment, and this container app -- because none of those live in
// `thinkschool-rg`, and the environment itself is referenced, not owned.
resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2023-05-01' existing = {
  name: 'thinkschool-env'
  scope: resourceGroup('thinkschool-rg')
}

// Managed Identity for QuotesApi
resource quotesApiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${abbreviations.managedIdentity}-quotes-api-${resourceToken}'
  location: location
  tags: tags
}

// Grants the container app's managed identity permission to pull from the
// registry without embedding the admin username/password as a container
// secret. AcrPull role definition ID is a fixed, well-known GUID (same
// across all tenants/subscriptions), not the registry's own resource ID.
resource acrPullRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(containerRegistry.id, quotesApiIdentity.id, 'AcrPull')
  scope: containerRegistry
  properties: {
    principalId: quotesApiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  }
}

// Container App Service (quotes-api)
//
// Named quotes-api-cowork, not quotes-api. The shared environment this
// exercise deploys into (see above) already had a container app named
// quotes-api-azd left over from a separate, concurrent attempt at this
// same exercise -- ContainerAppNameConflictInCluster on the first
// `azd up`. Container app names must be unique within an environment,
// not just within a resource group, so this exercise's app carries a
// distinguishing suffix. It was confirmed, before renaming, that the
// existing quotes-api-azd was not this deployment's own resource and
// was not serving traffic (stuck in ImagePullBackOff), so nothing here
// was renamed to avoid a collision with itself.
resource quotesApi 'Microsoft.App/containerApps@2023-05-01' = {
  name: 'quotes-api-cowork'
  location: location
  tags: union(tags, { 'azd-service-name': 'quotes-api' })
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${quotesApiIdentity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppsEnvironment.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
      }
      secrets: [
        {
          // A literal value here, by request, rather than the guid()
          // derivation this workspace used earlier. QuotesApi.Configuration
          // JwtOptions.MinLength(32) rejects anything shorter at startup
          // (HMAC-SHA256 needs a 256-bit key) -- the requested value alone
          // was 22 characters, so a fixed suffix pads it past that
          // threshold rather than changing its meaning. This is still not
          // a production-grade secret (it is not high-entropy, and it is
          // committed to source control as plaintext, delivered to the
          // container only via a Container Apps secret/secretRef rather
          // than an environment variable). It authenticates nothing
          // external -- only this exercise's own tokens -- which is the
          // only reason a readable, non-random value is acceptable here at
          // all.
          name: 'jwt-secret'
          value: 'Vaishalee-A41105222049-QuotesApiJwt2026'
        }
      ]
      registries: [
        {
          server: containerRegistry.properties.loginServer
          identity: quotesApiIdentity.id
        }
      ]
    }
    template: {
      containers: [
        {
          // Before the first `azd deploy`/`azd up`, quotesApiImageName is
          // empty and the app has to point at *some* image so the resource
          // can be created at all -- this placeholder is swapped for the
          // real quotes-api image on every subsequent azd run, which is why
          // quotesApiExists/quotesApiImageName are threaded through from
          // main.parameters.json in the first place (see azd's
          // host: containerapp convention).
          //
          // In practice this parameter carries the wrong repository path
          // (see docs/azd-deployment.md, "the image-path bug") every time
          // azd computes it, because this project's csproj pins
          // ContainerRepository: quotes-api rather than letting azd choose
          // its own path. A working deployment still needs one corrective
          // `az containerapp update --image <endpoint>/quotes-api:<tag>`
          // after every `azd up`/`azd deploy` -- documented, not automated
          // away, in docs/azd-deployment.md section 6.
          name: 'quotes-api'
          image: empty(quotesApiImageName) ? 'mcr.microsoft.com/azuredocs/aci-helloworld:latest' : quotesApiImageName
          env: [
            {
              name: 'Jwt__Secret'
              secretRef: 'jwt-secret'
            }
            {
              name: 'Jwt__Issuer'
              value: 'https://yourapp.com'
            }
            {
              name: 'Jwt__Audience'
              value: 'quotes-api'
            }
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: 'Production'
            }
            // The double underscore is the environment-variable spelling of
            // the ':' separator, so this arrives as the configuration key
            // `ApplicationInsights:ConnectionString` -- the exact key
            // ObservabilityExtensions reads. The exercise text names
            // APPLICATIONINSIGHTS_CONNECTION_STRING, which is the Azure
            // Monitor distro's own auto-discovery variable; this app never
            // reads it, because it passes the connection string to
            // UseAzureMonitor() explicitly rather than letting the distro
            // find one. Both are set: the first is what actually turns
            // telemetry on here, the second keeps the app consistent with
            // what Azure tooling (and anyone following the standard docs)
            // expects to find on a Container App.
            //
            // Not a secretRef, unlike Jwt__Secret: the connection string
            // carries an ingestion key, which grants write-only access to
            // this component and nothing else, and every azd quickstart
            // treats it as configuration. It is deliberately NOT emitted as
            // a Bicep output -- azd writes outputs into .azure/<env>/.env,
            // and .azure is not in this repository's .gitignore.
            {
              name: 'ApplicationInsights__ConnectionString'
              value: applicationInsights.properties.ConnectionString
            }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: applicationInsights.properties.ConnectionString
            }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/health/live'
                port: 8080
              }
              initialDelaySeconds: 5
              periodSeconds: 10
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health/ready'
                port: 8080
              }
              initialDelaySeconds: 5
              periodSeconds: 10
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 5
        rules: [
          {
            name: 'http-concurrency-rule'
            custom: {
              type: 'http'
              metadata: {
                concurrentRequests: '50'
              }
            }
          }
        ]
      }
    }
  }
}

output AZURE_CONTAINER_REGISTRY_ENDPOINT string = containerRegistry.properties.loginServer
output AZURE_CONTAINER_REGISTRY_NAME string = containerRegistry.name
output SERVICE_QUOTES_API_IDENTITY_PRINCIPAL_ID string = quotesApiIdentity.properties.principalId
output SERVICE_QUOTES_API_NAME string = quotesApi.name
output SERVICE_QUOTES_API_URI string = 'https://${quotesApi.properties.configuration.ingress.fqdn}'

// Names, not the connection string -- see the env block above for why the
// connection string itself stays out of azd's output/.env path. These are
// enough to open the resource, or to fetch the connection string on demand:
//   az monitor app-insights component show -g <rg> -a <name> --query connectionString -o tsv
output APPLICATIONINSIGHTS_NAME string = applicationInsights.name
output AZURE_LOG_ANALYTICS_WORKSPACE_NAME string = logAnalyticsWorkspace.name
