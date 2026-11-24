// One-time setup: lets GitHub Actions deploy to this resource group without any stored password.
// A person with Owner rights runs it once (handing out access needs more rights than deploying):
//
//   az deployment group create --resource-group tradeground --template-file infra/github-access.bicep
//
// How it works (OpenID Connect): when the deploy job runs, GitHub gives it a short-lived token saying
// "this is repo Goldengamer255/tradeground, deploying to the production environment". Azure checks that
// token against the federated credential below and, if it matches, lets the job act as this identity
// for a few minutes. There is no secret to leak, rotate or expire.

param location string = resourceGroup().location

param repository string = 'Goldengamer255/tradeground'

@description('The GitHub environment the deploy job uses. Only main may deploy to it (set in the repo settings).')
param environmentName string = 'production'

resource deployer 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'tradeground-github-deployer'
  location: location
}

resource github 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: deployer
  name: 'github-${environmentName}'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: 'repo:${repository}:environment:${environmentName}'
    audiences: ['api://AzureADTokenExchange']
  }
}

// Contributor on this resource group only: it can create and update the app here, but cannot touch
// anything else in the subscription or hand out access to anyone.
var contributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b24988ac-6180-42a0-ab88-20f7382dd24c')

resource canDeploy 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, deployer.id, contributor)
  properties: {
    roleDefinitionId: contributor
    principalId: deployer.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// These are identifiers, not secrets: they go in the repo's Actions variables.
output clientId string = deployer.properties.clientId
output tenantId string = deployer.properties.tenantId
