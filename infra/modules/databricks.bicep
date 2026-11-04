// Databricks stage B (approval-gated: tools/deploy_free.sh databricks … --approved-by). One SERVERLESS Azure Databricks
// workspace: serverless compute and default storage run in the Databricks account, so nothing else is created in the
// subscription (no managed resource group, VNet, NAT gateway, public IP, storage account or access connector; VERIFIED_FACTS
// F114-F115). Premium tier: identity federation, Unity Catalog and automatic identity management with Microsoft Entra ID
// (F116-F118). Pay per DBU only while serverless compute runs (F119). Needs Databricks stage A (policy) live first.
param tags object

@description('Region with serverless workspaces (Microsoft Learn feature-region-support, F115). centralindia is listed.')
param location string = 'centralindia'

@description('Workspace name. One workspace only (CLAUDE.md rule 1).')
param workspaceName string = 'dbw-pmo-${uniqueString(resourceGroup().id)}'

resource workspace 'Microsoft.Databricks/workspaces@2026-01-01' = {
  name: workspaceName
  location: location
  tags: tags
  sku: {
    name: 'premium'
  }
  properties: {
    computeMode: 'Serverless' // set at creation, cannot be changed; managedResourceGroupId is not allowed with it (F114)
    publicNetworkAccess: 'Enabled' // front-end private link would need private endpoints (CLAUDE.md rule 2)
  }
}

output workspaceName string = workspace.name
output workspaceUrl string = workspace.properties.workspaceUrl
output workspaceId string = workspace.properties.workspaceId
output workspaceResourceId string = workspace.id
