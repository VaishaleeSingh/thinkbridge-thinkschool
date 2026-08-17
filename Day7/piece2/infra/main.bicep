targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('Name of the environment which is used to generate a short unique hash for all resources.')
param environmentName string

@minLength(1)
@description('Primary location for all resources')
param location string

param quotesApiExists bool = false
param quotesApiImageName string = ''

var resourceToken = toLower(uniqueString(subscription().id, environmentName, location))
var tags = { 'azd-env-name': environmentName }

// Resource Group -- dedicated to this azd exercise (thinkschool-rg from the
// earlier manual-CLI exercise in this same repo is left untouched).
resource rg 'Microsoft.Resources/resourceGroups@2021-04-01' = {
  name: 'thinkschool-azd-rg'
  location: location
  tags: tags
}

module resources 'resources.bicep' = {
  name: 'resources'
  scope: rg
  params: {
    environmentName: environmentName
    location: location
    resourceToken: resourceToken
    tags: tags
    quotesApiExists: quotesApiExists
    quotesApiImageName: quotesApiImageName
  }
}

output AZURE_LOCATION string = location
output AZURE_TENANT_ID string = subscription().tenantId
output AZURE_RESOURCE_GROUP string = rg.name
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = resources.outputs.AZURE_CONTAINER_REGISTRY_ENDPOINT
output AZURE_CONTAINER_REGISTRY_NAME string = resources.outputs.AZURE_CONTAINER_REGISTRY_NAME
output SERVICE_QUOTES_API_IDENTITY_PRINCIPAL_ID string = resources.outputs.SERVICE_QUOTES_API_IDENTITY_PRINCIPAL_ID
output SERVICE_QUOTES_API_NAME string = resources.outputs.SERVICE_QUOTES_API_NAME
output SERVICE_QUOTES_API_URI string = resources.outputs.SERVICE_QUOTES_API_URI
output APPLICATIONINSIGHTS_NAME string = resources.outputs.APPLICATIONINSIGHTS_NAME
output AZURE_LOG_ANALYTICS_WORKSPACE_NAME string = resources.outputs.AZURE_LOG_ANALYTICS_WORKSPACE_NAME
