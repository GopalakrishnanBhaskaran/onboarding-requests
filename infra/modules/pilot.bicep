// Resource-group scope: guardrails first (policy, budget), then the two free-tier resources.
param location string
param swaLocation string
param budgetEmail string
param budgetAmount int
param budgetStartDate string
param tags object
@description('Phase 2: also allow the hand-off worker types (Function App, Flex plan, its storage account).')
param enableHandoff bool = false

var suffix = uniqueString(resourceGroup().id)
var cosmosName = 'cosmos-pmo-pilot-${suffix}'
var swaName = 'swa-pmo-pilot-${suffix}'
var databaseName = 'pmoreview'

// Built-in policy "Allowed resource types" (mode Indexed). Child types are listed explicitly because
// Indexed mode evaluates any type that supports tags + location (VERIFIED_FACTS F18, Q5).
var allowedResourceTypes = [
  'Microsoft.Web/staticSites'
  'Microsoft.Web/staticSites/config'
  'Microsoft.Web/staticSites/builds'
  'Microsoft.Web/staticSites/builds/config'
  'Microsoft.Web/staticSites/basicAuth'
  'Microsoft.DocumentDB/databaseAccounts'
  'Microsoft.DocumentDB/databaseAccounts/sqlDatabases'
  'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers'
  'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/throughputSettings'
  'Microsoft.Consumption/budgets'
]

// Phase 2 only (Gokul approved 16 Oct 2026, paid from Azure credits). Still no App Insights, Key Vault, OpenAI, VNets.
var handoffResourceTypes = [
  'Microsoft.Web/serverfarms'
  'Microsoft.Web/sites'
  'Microsoft.Web/sites/config'
  'Microsoft.Web/sites/basicPublishingCredentialsPolicies'
  'Microsoft.Web/sites/extensions'
  'Microsoft.Web/sites/functions'
  'Microsoft.Storage/storageAccounts'
  'Microsoft.Storage/storageAccounts/blobServices'
  'Microsoft.Storage/storageAccounts/blobServices/containers'
  'Microsoft.Storage/storageAccounts/queueServices'
  'Microsoft.Storage/storageAccounts/tableServices'
  'Microsoft.Storage/storageAccounts/fileServices'
  'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments'
]

resource allowedTypes 'Microsoft.Authorization/policyAssignments@2024-04-01' = {
  name: 'pmo-pilot-allowed-types'
  properties: {
    displayName: 'PMO pilot: allowed resource types (zero-cost guardrail)'
    description: enableHandoff
      ? 'Only Static Web Apps, Cosmos DB and the Phase 2 hand-off worker (Flex Function App + storage) may exist in this resource group.'
      : 'Only Static Web Apps and Cosmos DB (plus their child types) may exist in this resource group.'
    policyDefinitionId: tenantResourceId('Microsoft.Authorization/policyDefinitions', 'a08ec900-254a-4555-9bf5-e42af04b5c5c')
    enforcementMode: 'Default'
    parameters: {
      listOfResourceTypesAllowed: {
        value: enableHandoff ? concat(allowedResourceTypes, handoffResourceTypes) : allowedResourceTypes
      }
    }
  }
}

resource budget 'Microsoft.Consumption/budgets@2023-11-01' = {
  name: 'pmo-pilot-zero-cost'
  properties: {
    category: 'Cost'
    amount: budgetAmount
    timeGrain: 'Monthly'
    timePeriod: {
      startDate: budgetStartDate
    }
    // Phase 2: ₹2,000 cap paid from credits. Alerts at 50/80/100% actual and 100% forecast.
    notifications: {
      actual50: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 50
        thresholdType: 'Actual'
        contactEmails: [
          budgetEmail
        ]
      }
      actual80: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 80
        thresholdType: 'Actual'
        contactEmails: [
          budgetEmail
        ]
      }
      actual100: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 100
        thresholdType: 'Actual'
        contactEmails: [
          budgetEmail
        ]
      }
      forecast100: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 100
        thresholdType: 'Forecasted'
        contactEmails: [
          budgetEmail
        ]
      }
    }
  }
}

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' = {
  name: cosmosName
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  dependsOn: [
    allowedTypes
  ]
  properties: {
    databaseAccountOfferType: 'Standard'
    enableFreeTier: true // can only be set at creation; one free-tier account per subscription (F16)
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    enableAutomaticFailover: false
    enableMultipleWriteLocations: false
    enableAnalyticalStorage: false
    backupPolicy: {
      type: 'Periodic'
      periodicModeProperties: {
        backupIntervalInMinutes: 240
        backupRetentionIntervalInHours: 8
      }
    }
    minimalTlsVersion: 'Tls12'
    publicNetworkAccess: 'Enabled' // managed Functions have no VNet/private endpoint (paid); access is key-based
    disableKeyBasedMetadataWriteAccess: true // keys can read/write data, not create or change databases/containers
    capacity: {
      totalThroughputLimit: 1000 // hard account cap at the free-tier allowance (F13)
    }
  }
}

resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-11-15' = {
  parent: cosmos
  name: databaseName
  properties: {
    resource: {
      id: databaseName
    }
    options: {
      throughput: 1000 // shared, manual; no autoscale
    }
  }
}

// Must match src/adapters/PmoReview.Adapters/Storage/CosmosDocumentStore.cs (CosmosSchema).
var containers = [
  { name: 'projects', pk: '/projectId', uniqueKeys: [] }
  { name: 'requirementRules', pk: '/projectId', uniqueKeys: [ { paths: [ '/ruleId', '/version' ] } ] }
  { name: 'roleMappings', pk: '/projectId', uniqueKeys: [ { paths: [ '/mappingId', '/version' ] } ] }
  { name: 'requests', pk: '/projectId', uniqueKeys: [] }
  { name: 'reviewRuns', pk: '/projectId', uniqueKeys: [ { paths: [ '/idempotencyKey' ] } ] }
  { name: 'reviewDecisions', pk: '/projectId', uniqueKeys: [ { paths: [ '/reviewRunId', '/reviewer' ] } ] }
  { name: 'userAccess', pk: '/userId', uniqueKeys: [] }
  { name: 'handoffs', pk: '/projectId', uniqueKeys: [] }
]

resource sqlContainers 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = [for c in containers: {
  parent: database
  name: c.name
  properties: {
    resource: {
      id: c.name
      partitionKey: {
        paths: [
          c.pk
        ]
        kind: 'Hash'
      }
      uniqueKeyPolicy: {
        uniqueKeys: c.uniqueKeys
      }
    }
  }
}]

resource swa 'Microsoft.Web/staticSites@2024-04-01' = {
  name: swaName
  location: swaLocation
  tags: tags
  dependsOn: [
    allowedTypes
  ]
  sku: {
    name: 'Free'
    tier: 'Free'
  }
  properties: {
    allowConfigFileUpdates: true
    stagingEnvironmentPolicy: 'Disabled' // no preview environments in the pilot
  }
}

output staticWebAppName string = swa.name
output staticWebAppHostname string = swa.properties.defaultHostname
output cosmosAccountName string = cosmos.name
output policyAssignmentId string = allowedTypes.id
output budgetId string = budget.id
