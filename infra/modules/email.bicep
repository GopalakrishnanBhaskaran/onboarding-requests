// Mail stage B (approval-gated: tools/deploy_free.sh email … --approved-by). Azure Communication Services Email with an
// Azure-managed domain (DoNotReply@<guid>.azurecomm.net), linked to one Communication Services resource. The worker sends
// with its managed identity: one role assignment on the Communication Services resource only (no keys, no connection
// string). Pay per mail from Azure credits (VERIFIED_FACTS F64). Needs mail stage A (policy) live first.
param tags object

@description('Principal id of the worker managed identity (the only sender).')
param workerPrincipalId string

@description('Role the worker gets on the Communication Services resource. Default Contributor (resource scope only); deploy_free.sh passes "Communication and Email Service Owner" when the tenant lists it (F63).')
param senderRoleDefinitionId string = 'b24988ac-6180-42a0-ab88-20f7382dd24c'

@description('Where the mail data is stored at rest.')
param dataLocation string = 'India'

var suffix = uniqueString(resourceGroup().id)

resource emailService 'Microsoft.Communication/emailServices@2023-03-31' = {
  name: 'ecs-pmo-${suffix}'
  location: 'global'
  tags: tags
  properties: {
    dataLocation: dataLocation
  }
}

resource domain 'Microsoft.Communication/emailServices/domains@2023-03-31' = {
  parent: emailService
  name: 'AzureManagedDomain'
  location: 'global'
  tags: tags
  properties: {
    domainManagement: 'AzureManaged'
    userEngagementTracking: 'Disabled'
  }
}

resource acs 'Microsoft.Communication/communicationServices@2023-03-31' = {
  name: 'acs-pmo-${suffix}'
  location: 'global'
  tags: tags
  properties: {
    dataLocation: dataLocation
    linkedDomains: [
      domain.id
    ]
  }
}

resource sender 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acs.id, workerPrincipalId, senderRoleDefinitionId)
  scope: acs
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', senderRoleDefinitionId)
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output acsEndpoint string = 'https://${acs.properties.hostName}'
output senderAddress string = 'DoNotReply@${domain.properties.mailFromSenderDomain}'
output acsName string = acs.name
