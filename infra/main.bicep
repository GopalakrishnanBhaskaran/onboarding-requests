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

@description('Budget amount in the BILLING currency of the subscription (Q4: INR, so 1 = ₹1).')
@minValue(1)
param budgetAmount int = 1

@description('First day of the current month, yyyy-MM-01. Budgets must start on the first of a month.')
param budgetStartDate string

param tags object = {
  workload: 'pmo-review-pilot'
  costProfile: 'zero-cost-free-tier'
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
  }
}

output resourceGroupId string = rg.id
output staticWebAppName string = pilot.outputs.staticWebAppName
output staticWebAppHostname string = pilot.outputs.staticWebAppHostname
output cosmosAccountName string = pilot.outputs.cosmosAccountName
output policyAssignmentId string = pilot.outputs.policyAssignmentId
output budgetId string = pilot.outputs.budgetId
