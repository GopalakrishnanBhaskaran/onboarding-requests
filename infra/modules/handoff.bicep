// Phase 2 (paid from Azure credits, approved by Gokul 16 Oct 2026): Entra hand-off status sync worker.
// Flex Consumption Function App + its storage account. Managed identity only: no keys, no secrets.
//   Worker -> Cosmos: data-plane RBAC on the 'handoffs' container only.
//   Worker -> Storage: Storage Blob Data Owner (host locks + deployment package).
//   Worker -> Graph: EntitlementManagement.Read.All, granted by tools/setup_entra.sh (not ARM).
// No Application Insights / Log Analytics (CLAUDE.md rule 2).
param location string
param tags object
param cosmosAccountName string

@description('MUST match src/worker/PmoReview.Worker TargetFramework (test: InfraConsistencyTests). Check: az functionapp list-flexconsumption-runtimes -l <region> --runtime dotnet-isolated')
param workerRuntimeVersion string = '10.0'

var suffix = uniqueString(resourceGroup().id)
var storageName = 'stpmowork${suffix}'
var planName = 'asp-pmo-worker-${suffix}'
var workerName = 'func-pmo-worker-${suffix}'
var databaseName = 'pmoreview'
var deployContainerName = 'app-package'
var storageBlobDataOwner = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
var cosmosBuiltInDataContributor = '00000000-0000-0000-0000-000000000002'

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' existing = {
  name: cosmosAccountName
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS' // cheapest redundancy; holds only the app package and host locks
  }
  properties: {
    accessTier: 'Hot'
    allowSharedKeyAccess: false // identity only
    allowBlobPublicAccess: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource deployContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: deployContainerName
  properties: {
    publicAccess: 'None'
  }
}

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: planName
  location: location
  tags: tags
  kind: 'functionapp'
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  properties: {
    reserved: true
  }
}

resource worker 'Microsoft.Web/sites@2024-04-01' = {
  name: workerName
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: [
        { name: 'AzureWebJobsStorage__accountName', value: storage.name }
        { name: 'ALLOW_PAID_SERVICES', value: 'true' }
        { name: 'COSMOS_ENDPOINT', value: cosmos.properties.documentEndpoint }
        { name: 'COSMOS_DATABASE', value: databaseName }
        { name: 'HANDOFF_SYNC_SCHEDULE', value: '0 */5 * * * *' }
      ]
    }
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}${deployContainerName}'
          authentication: {
            type: 'SystemAssignedIdentity'
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: 40 // Flex minimum; a timer trigger uses one instance
        instanceMemoryMB: 512 // smallest size
      }
      runtime: {
        name: 'dotnet-isolated'
        version: workerRuntimeVersion
      }
    }
  }
}

// No basic-auth publishing (deploy uses Entra ID).
resource ftpPolicy 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: worker
  name: 'ftp'
  properties: {
    allow: false
  }
}

resource scmPolicy 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: worker
  name: 'scm'
  properties: {
    allow: false
  }
}

resource blobOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, worker.id, storageBlobDataOwner)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataOwner)
    principalId: worker.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Least privilege: data-plane access to the 'handoffs' container only. It cannot read requests, runs or userAccess.
resource cosmosHandoffWriter 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmos
  name: guid(cosmos.id, worker.id, cosmosBuiltInDataContributor, 'handoffs')
  properties: {
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/${cosmosBuiltInDataContributor}'
    principalId: worker.identity.principalId
    scope: '${cosmos.id}/dbs/${databaseName}/colls/handoffs'
  }
}

output workerName string = worker.name
output workerPrincipalId string = worker.identity.principalId
output storageAccountName string = storage.name
