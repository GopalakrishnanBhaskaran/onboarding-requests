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

@description('Auto-assign (approved by Gokul 16 Oct 2026): the worker files the Entra assignment after a second Reviewer accepts. Needs entraCatalogId.')
param enableAutoAssign bool = false

@description('GUID of the Entra catalog the worker may assign from (printed by: bash tools/setup_entra.sh package ...).')
param entraCatalogId string = ''

@description('Phase 2c (approved by Gokul 16 Oct 2026): create joiner accounts (P2 licence + Temporary Access Pass). Needs enableAutoAssign.')
param enableJoinerProvisioning bool = false

@description('Phase 2c: set up Entra groups, access packages and policies for published role mappings. Needs enableAutoAssign.')
param enablePackageAutoCreate bool = false

@description('Tenant domain, e.g. contoso.onmicrosoft.com (joiner UPNs, My Access links).')
param entraTenantDomain string = ''

@description('skuId of the Entra ID P2 licence for new joiner accounts.')
param entraLicenseSkuId string = ''

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
  }
}

module handoff 'modules/handoff.bicep' = if (enableHandoff && deployWorker) {
  name: 'pmo-review-handoff'
  scope: rg
  params: {
    location: workerLocation
    tags: tags
    cosmosAccountName: pilot.outputs.cosmosAccountName
    workerRuntimeVersion: workerRuntimeVersion
    autoAssign: enableAutoAssign
    entraCatalogId: entraCatalogId
    provisionJoiners: enableAutoAssign && enableJoinerProvisioning
    autoCreatePackages: enableAutoAssign && enablePackageAutoCreate
    entraTenantDomain: entraTenantDomain
    entraLicenseSkuId: entraLicenseSkuId
  }
}

output resourceGroupId string = rg.id
output staticWebAppName string = pilot.outputs.staticWebAppName
output staticWebAppHostname string = pilot.outputs.staticWebAppHostname
output cosmosAccountName string = pilot.outputs.cosmosAccountName
output policyAssignmentId string = pilot.outputs.policyAssignmentId
output budgetId string = pilot.outputs.budgetId
output workerName string = enableHandoff && deployWorker ? handoff!.outputs.workerName : ''
output workerPrincipalId string = enableHandoff && deployWorker ? handoff!.outputs.workerPrincipalId : ''
