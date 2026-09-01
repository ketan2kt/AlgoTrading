targetScope = 'resourceGroup'

@description('Azure region for application and database resources.')
param location string = 'centralindia'

@minLength(3)
@maxLength(18)
@description('Lowercase deployment prefix used in globally unique resource names.')
param prefix string

@secure()
@minLength(16)
@description('PostgreSQL administrator password. Supply from GitHub environment secrets.')
param postgresAdministratorPassword string

@allowed([
  'Paper'
])
param tradingMode string = 'Paper'

@description('Enables the separately armed Groww live-order worker while strategy evaluation remains in paper/shadow mode.')
param liveExecutionBuildEnabled bool = false

var normalizedPrefix = toLower(replace(prefix, '-', ''))
var deploymentSuffix = uniqueString(resourceGroup().id)
var postgresServerName = '${normalizedPrefix}pg${deploymentSuffix}'
var webAppName = '${normalizedPrefix}web${deploymentSuffix}'
var appServicePlanName = '${prefix}-linux-b1'
var postgresDatabaseName = 'trading_system'
var postgresAdministratorLogin = 'tradingadmin'

resource network 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: '${prefix}-vnet'
  location: location
  properties: {
    addressSpace: {
      addressPrefixes: [
        '10.40.0.0/16'
      ]
    }
    subnets: [
      {
        name: 'container-apps'
        properties: {
          addressPrefix: '10.40.0.0/23'
          delegations: [
            {
              name: 'Microsoft.App.environments'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ]
        }
      }
      {
        name: 'postgresql'
        properties: {
          addressPrefix: '10.40.2.0/24'
          delegations: [
            {
              name: 'Microsoft.DBforPostgreSQL.flexibleServers'
              properties: {
                serviceName: 'Microsoft.DBforPostgreSQL/flexibleServers'
              }
            }
          ]
        }
      }
      {
        name: 'app-service'
        properties: {
          addressPrefix: '10.40.3.0/26'
          delegations: [
            {
              name: 'Microsoft.Web.serverFarms'
              properties: {
                serviceName: 'Microsoft.Web/serverFarms'
              }
            }
          ]
        }
      }
    ]
  }
}

resource postgresSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' existing = {
  name: 'postgresql'
  parent: network
}

resource appServiceSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' existing = {
  name: 'app-service'
  parent: network
}

resource postgresPrivateDns 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: 'privatelink.postgres.database.azure.com'
  location: 'global'
}

resource postgresDnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  name: '${prefix}-postgres-link'
  parent: postgresPrivateDns
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: network.id
    }
  }
}

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: postgresServerName
  location: location
  sku: {
    name: 'Standard_B1ms'
    tier: 'Burstable'
  }
  properties: {
    version: '16'
    administratorLogin: postgresAdministratorLogin
    administratorLoginPassword: postgresAdministratorPassword
    availabilityZone: '1'
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: {
      mode: 'Disabled'
    }
    network: {
      delegatedSubnetResourceId: postgresSubnet.id
      privateDnsZoneArmResourceId: postgresPrivateDns.id
      publicNetworkAccess: 'Disabled'
    }
    storage: {
      storageSizeGB: 32
      autoGrow: 'Enabled'
    }
  }
  dependsOn: [
    postgresDnsLink
  ]
}

resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  name: postgresDatabaseName
  parent: postgres
  properties: {}
}

resource appServicePlan 'Microsoft.Web/serverfarms@2024-11-01' = {
  name: appServicePlanName
  location: location
  kind: 'linux'
  sku: {
    name: 'B1'
    tier: 'Basic'
    capacity: 1
  }
  properties: {
    reserved: true
  }
}

resource webApp 'Microsoft.Web/sites@2024-11-01' = {
  name: webAppName
  location: location
  kind: 'app,linux'
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    virtualNetworkSubnetId: appServiceSubnet.id
    siteConfig: {
      alwaysOn: true
      ftpsState: 'Disabled'
      healthCheckPath: '/health/live'
      http20Enabled: true
      linuxFxVersion: 'DOTNETCORE|10.0'
      minTlsVersion: '1.2'
      appSettings: [
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Production'
        }
        {
          name: 'Trading__Mode'
          value: tradingMode
        }
        {
          name: 'AllowedHosts'
          value: '*'
        }
        {
          name: 'DataProtection__KeysPath'
          value: '/home/data-protection-keys'
        }
        {
          name: 'DatabaseInitialization__ApplyMigrations'
          value: 'false'
        }
        {
          name: 'MarketData__LiveNifty__Enabled'
          value: 'true'
        }
        {
          name: 'PaperTrading__Automation__Enabled'
          value: 'true'
        }
        {
          name: 'LiveExecution__BuildEnabled'
          value: string(liveExecutionBuildEnabled)
        }
        {
          name: 'LiveExecution__MaximumLotsPerOrder'
          value: '5'
        }
        {
          name: 'LiveExecution__ControlledTestLotsPerOrder'
          value: '1'
        }
        {
          name: 'ConnectionStrings__TradingDatabase'
          value: 'Host=${postgres.properties.fullyQualifiedDomainName};Port=5432;Database=${postgresDatabaseName};Username=${postgresAdministratorLogin};Password=${postgresAdministratorPassword};SSL Mode=Require;Trust Server Certificate=false'
        }
      ]
    }
  }
  dependsOn: [
    database
  ]
}

output webAppName string = webApp.name
output webAppHostName string = webApp.properties.defaultHostName
output postgresServerName string = postgres.name
