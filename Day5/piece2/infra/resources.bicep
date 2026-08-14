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

// Log Analytics workspace backing the Container Apps Environment. A new,
// dedicated environment is provisioned here (thinkschool-azd-env) rather than
// reusing the thinkschool-env created by the earlier manual az-cli exercise
// (Day5/piece2/scripts/deploy-aca.ps1) in this same repo, so the two
// exercises stay fully independent and `azd down` cannot delete
// infrastructure the other exercise still owns.
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: '${abbreviations.logAnalyticsWorkspace}-${resourceToken}'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
  tags: tags
}

resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2023-05-01' = {
  name: 'thinkschool-azd-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
  tags: tags
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
resource quotesApi 'Microsoft.App/containerApps@2023-05-01' = {
  name: 'quotes-api'
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
          name: 'quotes-api'
          image: empty(quotesApiImageName) ? 'mcr.microsoft.com/azuredocs/aci-helloworld:latest' : quotesApiImageName
          env: [
            {
              name: 'Jwt__Secret'
              value: 'SuperSecretKeyForJwtAuthenticationMustBeAtLeast32BytesLong!'
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
