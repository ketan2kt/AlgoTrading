targetScope = 'resourceGroup'

param location string = 'centralindia'
param webAppName string = 'sarthitradingweb5amvyarepxaci'
param recorderName string = 'sarthirecorder5amvyarepxaci'
param storageName string = 'sarthireplay2026'
param subnetId string
param webPrincipalId string

resource storage 'Microsoft.Storage/storageAccounts@2025-01-01' = {
  name: storageName
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    accessTier: 'Hot'
  }
}
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2025-01-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: { enabled: true, days: 14 }
    containerDeleteRetentionPolicy: { enabled: true, days: 14 }
    isVersioningEnabled: true
  }
}
resource archive 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' = {
  parent: blobs
  name: 'replay'
  properties: { publicAccess: 'None' }
}
resource keys 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' = {
  parent: blobs
  name: 'protected-keys'
  properties: { publicAccess: 'None' }
}
resource plan 'Microsoft.Web/serverfarms@2024-11-01' = {
  name: 'sarthi-recorder-linux-b1'
  location: location
  kind: 'linux'
  sku: { name: 'B1', tier: 'Basic', capacity: 1 }
  properties: { reserved: true }
}
resource recorder 'Microsoft.Web/sites@2024-11-01' = {
  name: recorderName
  location: location
  kind: 'app,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    virtualNetworkSubnetId: subnetId
    siteConfig: {
      alwaysOn: true
      linuxFxVersion: 'DOTNETCORE|10.0'
      appCommandLine: 'dotnet TradingSystem.Api.dll'
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      healthCheckPath: '/health/live'
      appSettings: [
        { name: 'ApplicationRole', value: 'ReplayRecorder' }
        { name: 'ReplayStorage__ContainerUri', value: '${storage.properties.primaryEndpoints.blob}replay' }
        { name: 'ReplayStorage__KeyRingUri', value: '${storage.properties.primaryEndpoints.blob}protected-keys/keyring.xml' }
        { name: 'MarketData__HistoricalReplayFeed__Enabled', value: 'false' }
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
      ]
    }
  }
}
var contributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
var reader = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')
resource webArchiveAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: archive
  name: guid(archive.id, webPrincipalId, contributor)
  properties: { principalId: webPrincipalId, roleDefinitionId: contributor, principalType: 'ServicePrincipal' }
}
resource recorderArchiveAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: archive
  name: guid(archive.id, recorder.id, contributor)
  properties: { principalId: recorder.identity.principalId, roleDefinitionId: contributor, principalType: 'ServicePrincipal' }
}
resource webKeyAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keys
  name: guid(keys.id, webPrincipalId, contributor)
  properties: { principalId: webPrincipalId, roleDefinitionId: contributor, principalType: 'ServicePrincipal' }
}
resource recorderKeyAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keys
  name: guid(keys.id, recorder.id, reader)
  properties: { principalId: recorder.identity.principalId, roleDefinitionId: reader, principalType: 'ServicePrincipal' }
}
output archiveUri string = '${storage.properties.primaryEndpoints.blob}replay'
output keyRingUri string = '${storage.properties.primaryEndpoints.blob}protected-keys/keyring.xml'
output recorderApp string = recorder.name
output websiteApp string = webAppName
