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
          // Derived, not typed or pasted anywhere. guid() with these three
          // inputs is deterministic per subscription/environment but not
          // guessable, and it never appears as a literal string in this
          // repository, an azd command, or a terminal -- unlike the
          // original draft, which had this value hardcoded as
          // @secure() param jwtSecret string with a literal placeholder
          // default. It authenticates nothing external; it only needs to
          // be a stable, non-guessable signing key for this exercise's
          // own tokens.
          name: 'jwt-secret'
          value: guid(subscription().id, resourceToken, 'jwt-secret')
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
              name: 'ASPNETCORE_ENVIRONMENT'
              value: 'Production'
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
