// The AI agent's background worker (Phase 2 resources, paid from Azure credits, approved by Gokul 16 Oct 2026).
// Flex Consumption Function App + its storage account. Managed identity only: no keys, no secrets.
//   Worker -> Cosmos: data-plane RBAC scoped to the containers it uses (requests, reviewRuns, mappingSheet,
//             responseDocument, notifications, issues; v1.1: peopleInventory, escalations, mappingQualityIssues,
//             groupResolutions, offboardingRuns, auditEvents) and read-only on projects. Never userAccess or the upload versions.
//   Worker -> Storage: Storage Blob Data Owner (host locks + deployment package).
//   Worker -> Microsoft Graph: READ ONLY (User.Read.All, GroupMember.Read.All, UserAuthenticationMethod.Read.All,
//             AuditLog.Read.All), granted by tools/deploy_free.sh grant-graph, not ARM. The AI agent never approves,
//             creates or assigns anything: the ID team and the role assignment team do that.
//   Optional: Reader on the Azure scopes whose role assignments it verifies (ROLE_READER=Arm), granted per scope by an
//             administrator; Azure DevOps access by adding this identity to the ADO organisation.
// No Application Insights / Log Analytics / Key Vault / Azure OpenAI here (CLAUDE.md rule 2).
param location string
param tags object
param cosmosAccountName string

@description('MUST match src/worker/PmoReview.Worker TargetFramework (test: InfraConsistencyTests). Check: az functionapp list-flexconsumption-runtimes -l <region> --runtime dotnet-isolated')
param workerRuntimeVersion string = '10.0'

@description('Outbox (pilot: notifications stay in the app) or GraphMail (needs notifyFrom and Mail.Send limited to that mailbox).')
@allowed([
  'Outbox'
  'GraphMail'
  'AcsEmail'
])
param notifySender string = 'Outbox'

@description('Shared sender mailbox for GraphMail, or DoNotReply@<managed domain> for AcsEmail. Empty for Outbox.')
param notifyFrom string = ''

@description('Azure Communication Services endpoint for AcsEmail. Empty otherwise.')
param acsEndpoint string = ''

@description('pilot, dev, test or prod.')
param appEnvironment string = 'pilot'

@description('Non-production only: every mail goes to this test address.')
param notifyRedirectTo string = ''

@description('Entra tenant id of the AI agent (tenant separation).')
param agentTenantId string = ''

@description('Azure DevOps organisation URL (e.g. https://dev.azure.com/contoso). Empty = no work items.')
param adoOrgUrl string = ''

@description('Azure DevOps project for the work items.')
param adoProject string = ''

@description('None, or Arm to verify Azure RBAC role assignments (needs Reader on the verified scopes).')
@allowed([
  'None'
  'Arm'
])
param roleReader string = 'None'

@description('App URL for links in notifications (https://...azurestaticapps.net).')
param appUrl string = ''

@description('Maximum Temporary Access Pass lifetime the ID team may record (shortest lifetime).')
param tapMaxLifetimeMinutes int = 60

@description('v2.1: after both approvals the agent creates the user, group and membership. Needs the approved Graph write roles (deploy_free.sh provisioning).')
param agentProvisioning bool = false

@description('v2.1: after both approvals the agent assigns the mapping row Azure role to the group. Needs the approved constrained RBAC Administrator role.')
param agentRoleAssignment bool = false

@description('Comma-separated Azure role definition GUIDs the agent may assign (allow-list).')
param roleAssignAllowedRoles string = ''

@description('Agent TAP: for an account it just created, the agent mails a one-time TAP + initial password to the joiner. Approval-gated: deploy_free.sh tap.')
param agentTap bool = false

@description('Lifetime in minutes of an agent-issued TAP (within the tenant TAP policy maximum).')
param agentTapLifetimeMinutes int = 480

@description('Agent privileged rows: also provision rows flagged privileged after both approvals (never privileged administrator roles). Approval-gated.')
param agentPrivileged bool = false

@description('v1.1 F5: the worker executes confirmed offboarding plans (revoke, remove, disable). Off until its permissions are approved.')
param agentOffboarding bool = false

@description('v1.1 F5: after disabling, soft-delete the account (restorable 30 days). Off by default; needs User.ReadWrite.All (not granted).')
param offboardDeleteUser bool = false

@description('v1.1 F5: comma-separated subscription ids whose Azure role assignments offboarding removes (empty = every subscription the worker can list).')
param offboardSubscriptions string = ''

@description('v1.1 F2: the agent TAP mail sender mailbox (TAP_MAIL_SENDER). Kept here so a redeploy never drops it: the settings below replace all of the worker\'s app settings.')
param tapMailSender string = ''

@description('MAIL_MAX_PER_MINUTE / MAIL_MAX_PER_HOUR (empty = defaults, F70).')
param mailMaxPerMinute string = ''
param mailMaxPerHour string = ''

@description('Comma-separated Azure scopes (prefixes) where the agent may assign roles.')
param roleAssignScopes string = ''

var suffix = uniqueString(resourceGroup().id)
var storageName = 'stpmowork${suffix}'
var planName = 'asp-pmo-worker-${suffix}'
var workerName = 'func-pmo-worker-${suffix}'
var databaseName = 'pmoreview'
var deployContainerName = 'app-package'
var storageBlobDataOwner = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
var cosmosBuiltInDataReader = '00000000-0000-0000-0000-000000000001'
var cosmosBuiltInDataContributor = '00000000-0000-0000-0000-000000000002'
// Least privilege: only the containers the agent reads and writes. It never reads userAccess.
var writableContainers = [
  'requests'
  'reviewRuns'
  'mappingSheet'
  'responseDocument'
  'notifications'
  'issues'
  // v1.1: TAP recipient and leavers, escalations, mapping quality, group resolutions, offboarding, audit.
  'peopleInventory'
  'escalations'
  'mappingQualityIssues'
  'groupResolutions'
  'offboardingRuns'
  'auditEvents'
]

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
        { name: 'WORKFLOW_SCHEDULE', value: '0 */5 * * * *' }
        { name: 'REVIEW_MODEL_PROVIDER', value: 'RuleOnly' }
        { name: 'ROLE_READER', value: roleReader }
        { name: 'NOTIFY_SENDER', value: notifySender }
        { name: 'NOTIFY_FROM', value: notifyFrom }
        { name: 'ACS_ENDPOINT', value: acsEndpoint }
        { name: 'APP_ENVIRONMENT', value: appEnvironment }
        { name: 'NOTIFY_REDIRECT_TO', value: notifyRedirectTo }
        { name: 'AGENT_TENANT_ID', value: agentTenantId }
        { name: 'ADO_ORG_URL', value: adoOrgUrl }
        { name: 'ADO_PROJECT', value: adoProject }
        { name: 'APP_URL', value: appUrl }
        { name: 'TAP_MAX_LIFETIME_MINUTES', value: string(tapMaxLifetimeMinutes) }
        { name: 'AGENT_PROVISIONING', value: agentProvisioning ? 'true' : 'false' }
        { name: 'AGENT_ROLE_ASSIGNMENT', value: agentRoleAssignment ? 'true' : 'false' }
        { name: 'ROLE_ASSIGN_ALLOWED_ROLES', value: roleAssignAllowedRoles }
        { name: 'ROLE_ASSIGN_SCOPES', value: roleAssignScopes }
        { name: 'AGENT_TAP', value: agentTap ? 'true' : 'false' }
        { name: 'AGENT_TAP_LIFETIME_MINUTES', value: string(agentTapLifetimeMinutes) }
        { name: 'AGENT_PRIVILEGED', value: agentPrivileged ? 'true' : 'false' }
        { name: 'AGENT_OFFBOARDING', value: agentOffboarding ? 'true' : 'false' }
        { name: 'OFFBOARD_DELETE_USER', value: offboardDeleteUser ? 'true' : 'false' }
        { name: 'OFFBOARD_SUBSCRIPTIONS', value: offboardSubscriptions }
        { name: 'TAP_MAIL_SENDER', value: tapMailSender }
        { name: 'MAIL_MAX_PER_MINUTE', value: mailMaxPerMinute }
        { name: 'MAIL_MAX_PER_HOUR', value: mailMaxPerHour }
        // Retired Phase 2b/2c switches: the worker refuses to start if any is true.
        { name: 'ENTRA_AUTO_ASSIGN', value: 'false' }
        { name: 'ENTRA_PROVISION_JOINERS', value: 'false' }
        { name: 'ENTRA_AUTO_CREATE_PACKAGES', value: 'false' }
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

resource cosmosWriters 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = [for c in writableContainers: {
  parent: cosmos
  name: guid(cosmos.id, worker.id, cosmosBuiltInDataContributor, c)
  properties: {
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/${cosmosBuiltInDataContributor}'
    principalId: worker.identity.principalId
    scope: '${cosmos.id}/dbs/${databaseName}/colls/${c}'
  }
}]

// Project contacts and naming conventions: read only.
resource cosmosProjectsReader 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmos
  name: guid(cosmos.id, worker.id, cosmosBuiltInDataReader, 'projects')
  properties: {
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/${cosmosBuiltInDataReader}'
    principalId: worker.identity.principalId
    scope: '${cosmos.id}/dbs/${databaseName}/colls/projects'
  }
}

output workerName string = worker.name
output workerPrincipalId string = worker.identity.principalId
output storageAccountName string = storage.name
