// PMO Request Review — zero-cost pilot.
// Subscription scope so the resource group itself goes through the same what-if + approval gate.
// Deploy ONLY after Gokul says "approved" on the what-if output (CLAUDE.md rule 4).
targetScope = 'subscription'

@description('Dedicated pilot resource group.')
param resourceGroupName string = 'rg-pmo-review-pilot'

@description('Region for the resource group and Cosmos DB. Confirm with Gokul (Phase 0 Q6).')
param location string = 'centralindia'

@description('Static Web Apps has a limited region list for the Free plan + managed Functions. Confirm before deploy.')
@allowed([
  'centralus'
  'eastus2'
  'eastasia'
  'westeurope'
  'westus2'
])
param swaLocation string = 'eastasia'

@description('Budget alert recipient.')
param budgetEmail string

@description('Monthly budget in the BILLING currency (INR). Phase 2 cap approved by Gokul: 2000 (alerts at 50/80/100%).')
@minValue(1)
param budgetAmount int = 2000

@description('Phase 2 stage A: allow the worker resource types in the policy, add the handoffs container, Rs 2,000 budget. Creates nothing paid.')
param enableHandoff bool = false

@description('Phase 2 stage B: deploy the hand-off sync worker (Flex Consumption Function App + storage), paid from Azure credits. Only after stage A is live: Azure checks new resources against the CURRENT policy.')
param deployWorker bool = false

@description('Region for the worker. Must be listed by: az functionapp list-flexconsumption-locations')
param workerLocation string = location

@description('Worker .NET version on Flex Consumption. Must match the worker TargetFramework.')
param workerRuntimeVersion string = '10.0'

@description('Notifications: Outbox (pilot, kept in the app), GraphMail (needs notifyFrom) or AcsEmail (needs acsEndpoint + notifyFrom; deploy_free.sh email).')
param notifySender string = 'Outbox'

@description('Sender: the shared mailbox for GraphMail, or DoNotReply@<managed domain> for AcsEmail.')
param notifyFrom string = ''

@description('Mail stage A: allow the Azure Communication Services Email types in the policy (approval-gated, creates nothing).')
param enableEmail bool = false

@description('Azure Communication Services endpoint for AcsEmail (https://<name>.communication.azure.com).')
param acsEndpoint string = ''

@description('Deployment environment: pilot, dev, test or prod. Shown in the app; test mail subjects carry it.')
@allowed([
  'pilot'
  'dev'
  'test'
  'prod'
])
param appEnvironment string = 'pilot'

@description('Non-production only: every mail goes to this one test address (the intended recipients are named in the mail).')
param notifyRedirectTo string = ''

@description('Entra tenant id of the AI agent (tenant separation: projects bound to another tenant are never read or changed by the agent).')
param agentTenantId string = tenant().tenantId

@description('Azure DevOps organisation URL for request work items. Empty = off.')
param adoOrgUrl string = ''

@description('Azure DevOps project for request work items.')
param adoProject string = ''

@description('None, or Arm to verify Azure RBAC role assignments read-only (needs Reader on the verified scopes).')
param roleReader string = 'None'

@description('App URL used in notification links.')
param appUrl string = ''

@description('v2.1 agent provisioning (user, group, membership) after both approvals. Approval-gated: deploy_free.sh provisioning.')
param agentProvisioning bool = false

@description('v2.1 agent role assignment (allow-listed Azure roles on the group) after both approvals. Approval-gated.')
param agentRoleAssignment bool = false

@description('Comma-separated Azure role definition GUIDs the agent may assign.')
param roleAssignAllowedRoles string = ''

@description('Comma-separated Azure scopes (prefixes) where the agent may assign roles.')
param roleAssignScopes string = ''

@description('Agent TAP (one-time TAP + initial password mailed to the joiner for an account the agent created). Approval-gated: deploy_free.sh tap.')
param agentTap bool = false

@description('Lifetime in minutes of an agent-issued TAP.')
param agentTapLifetimeMinutes int = 480

@description('First day of the current month, yyyy-MM-01. Budgets must start on the first of a month.')
param budgetStartDate string

param tags object = {
  workload: 'pmo-review-pilot'
  costProfile: 'free-tier-plus-phase2-credits'
  dataClassification: 'sample-only'
}

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

module pilot 'modules/pilot.bicep' = {
  name: 'pmo-review-pilot'
  scope: rg
  params: {
    location: location
    swaLocation: swaLocation
    budgetEmail: budgetEmail
    budgetAmount: budgetAmount
    budgetStartDate: budgetStartDate
    tags: tags
    enableHandoff: enableHandoff
    enableEmail: enableEmail
  }
}

module worker 'modules/worker.bicep' = if (enableHandoff && deployWorker) {
  name: 'pmo-review-worker'
  scope: rg
  params: {
    location: workerLocation
    tags: tags
    cosmosAccountName: pilot.outputs.cosmosAccountName
    workerRuntimeVersion: workerRuntimeVersion
    notifySender: notifySender
    notifyFrom: notifyFrom
    acsEndpoint: acsEndpoint
    appEnvironment: appEnvironment
    notifyRedirectTo: notifyRedirectTo
    agentTenantId: agentTenantId
    adoOrgUrl: adoOrgUrl
    adoProject: adoProject
    roleReader: roleReader
    appUrl: appUrl
    agentProvisioning: agentProvisioning
    agentRoleAssignment: agentRoleAssignment
    roleAssignAllowedRoles: roleAssignAllowedRoles
    roleAssignScopes: roleAssignScopes
    agentTap: agentTap
    agentTapLifetimeMinutes: agentTapLifetimeMinutes
  }
}

output resourceGroupId string = rg.id
output staticWebAppName string = pilot.outputs.staticWebAppName
output staticWebAppHostname string = pilot.outputs.staticWebAppHostname
output cosmosAccountName string = pilot.outputs.cosmosAccountName
output policyAssignmentId string = pilot.outputs.policyAssignmentId
output budgetId string = pilot.outputs.budgetId
output workerName string = enableHandoff && deployWorker ? worker!.outputs.workerName : ''
output workerPrincipalId string = enableHandoff && deployWorker ? worker!.outputs.workerPrincipalId : ''
