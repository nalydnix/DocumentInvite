// DocumentInvite Azure resources: App Service (Linux, .NET 10), Azure SQL (free offer) and a Storage
// account with a private container. No secrets are stored here; the SQL admin password is a secure
// parameter supplied at deployment time (infra/provision.sh or infra/provision.ps1).
targetScope = 'resourceGroup'

@description('Prefix for every resource name, 3 to 12 lowercase letters or digits.')
@minLength(3)
@maxLength(12)
param namePrefix string

@description('Azure region.')
param location string = resourceGroup().location

@description('Origin of the web app (for example https://app.example.com). Used for the Blob CORS rule.')
param webOrigin string

@description('App Service plan SKU. B1 is the cheapest tier that supports Linux always-on.')
param appServiceSku string = 'B1'

@description('Azure SQL administrator login.')
param sqlAdminLogin string = 'diadmin'

@description('Azure SQL administrator password.')
@secure()
param sqlAdminPassword string

@description('Name of the private blob container for uploaded documents.')
param blobContainerName string = 'documents'

var suffix = uniqueString(resourceGroup().id)
var planName = '${namePrefix}-plan'
var webAppName = '${namePrefix}-api-${suffix}'
var sqlServerName = '${namePrefix}-sql-${suffix}'
var sqlDatabaseName = 'DocumentInvite'
var storageAccountName = toLower(take('${namePrefix}${suffix}', 24))

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: planName
  location: location
  kind: 'linux'
  sku: {
    name: appServiceSku
  }
  properties: {
    reserved: true
  }
}

resource webApp 'Microsoft.Web/sites@2023-12-01' = {
  name: webAppName
  location: location
  kind: 'app,linux'
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: true
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      healthCheckPath: '/health'
    }
  }
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: location
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

// Lets Azure services (the App Service) reach the server. Add your own IP to run migrations by hand.
resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: sqlDatabaseName
  location: location
  sku: {
    name: 'GP_S_Gen5_2'
    tier: 'GeneralPurpose'
  }
  properties: {
    useFreeLimit: true
    freeLimitExhaustionBehavior: 'AutoPause'
    autoPauseDelay: 60
    minCapacity: json('0.5')
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    cors: {
      corsRules: [
        {
          allowedOrigins: [ webOrigin ]
          allowedMethods: [ 'PUT', 'GET', 'HEAD', 'OPTIONS' ]
          allowedHeaders: [ 'x-ms-*', 'content-type' ]
          exposedHeaders: [ 'x-ms-*', 'ETag' ]
          maxAgeInSeconds: 3600
        }
      ]
    }
  }
}

resource container 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: blobContainerName
  properties: {
    publicAccess: 'None'
  }
}

output apiUrl string = 'https://${webApp.properties.defaultHostName}'
output webAppName string = webApp.name
output appServicePlanName string = plan.name
output sqlServerName string = sqlServer.name
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output sqlDatabaseName string = sqlDatabase.name
output storageAccountName string = storage.name
output blobContainerName string = container.name
