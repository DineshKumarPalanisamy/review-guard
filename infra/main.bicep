// Review Guard infrastructure. One resource group, Flex Consumption Function app.
// No keys in app settings: the Function uses its Managed Identity for Storage and Key Vault.
// The Azure DevOps PAT lives in Key Vault (secret "AdoPat"), set by a person, never by this file.

@description('Azure region. Flex Consumption must be available there.')
param location string = resourceGroup().location

@description('Short name used in all resource names.')
param appName string = 'review-guard'

@description('Azure DevOps organization URL.')
param azureDevOpsOrgUrl string = 'https://dev.azure.com/DigiCovai'

@description('Object id of the person who deploys. Gets Key Vault Secrets Officer to set the secrets.')
param deployerObjectId string

@description('Email address that gets the fast-approval alert. Empty = no alert resources.')
param alertEmail string = ''

@description('Rule thresholds (DESIGN.md section 6).')
param minLines int = 200
param secondsPer100Lines int = 30

var suffix = uniqueString(resourceGroup().id)
var storageName = take('stgrg${replace(appName, '-', '')}${suffix}', 24)
var keyVaultName = take('kv-${appName}-${suffix}', 24)
var functionAppName = '${appName}-${suffix}'
var deploymentContainer = 'deploymentpackage'

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${appName}'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${appName}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
    DisableLocalAuth: false
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false // Managed Identity only
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }

  resource blob 'blobServices' = {
    name: 'default'
    resource container 'containers' = {
      name: deploymentContainer
    }
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
  }
}

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: 'asp-${appName}'
  location: location
  kind: 'functionapp'
  sku: { name: 'FC1', tier: 'FlexConsumption' }
  properties: { reserved: true }
}

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: functionAppName
  location: location
  kind: 'functionapp,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: [
        { name: 'AzureWebJobsStorage__accountName', value: storage.name }
        { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
        { name: 'AuditTableServiceUri', value: storage.properties.primaryEndpoints.table }
        { name: 'ReviewGuard__OrganizationUrl', value: azureDevOpsOrgUrl }
        { name: 'ReviewGuard__MinLines', value: string(minLines) }
        { name: 'ReviewGuard__SecondsPer100Lines', value: string(secondsPer100Lines) }
        { name: 'ReviewGuard__Pat', value: '@Microsoft.KeyVault(VaultName=${keyVault.name};SecretName=AdoPat)' }
        { name: 'ReviewGuard__WebhookSecret', value: '@Microsoft.KeyVault(VaultName=${keyVault.name};SecretName=WebhookSecret)' }
      ]
    }
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}${deploymentContainer}'
          authentication: { type: 'SystemAssignedIdentity' }
        }
      }
      runtime: { name: 'dotnet-isolated', version: '10.0' }
      scaleAndConcurrency: {
        maximumInstanceCount: 40
        instanceMemoryMB: 2048
      }
    }
  }
}

// Built-in role ids.
var roles = {
  storageBlobDataOwner: 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
  storageQueueDataContributor: '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
  storageTableDataContributor: '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  keyVaultSecretsOfficer: 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
  monitoringMetricsPublisher: '3913510d-42f4-4e42-8a64-420c390055eb'
}

var functionStorageRoles = [roles.storageBlobDataOwner, roles.storageQueueDataContributor, roles.storageTableDataContributor]

resource functionStorageAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for role in functionStorageRoles: {
  name: guid(storage.id, functionApp.id, role)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', role)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}]

resource functionKeyVaultAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, functionApp.id, roles.keyVaultSecretsUser)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsUser)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource deployerKeyVaultAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, deployerObjectId, roles.keyVaultSecretsOfficer)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsOfficer)
    principalId: deployerObjectId
    principalType: 'User'
  }
}

// So the deployer can read the audit tables with Azure AD (shared keys are off).
resource deployerTableAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, deployerObjectId, roles.storageTableDataContributor)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageTableDataContributor)
    principalId: deployerObjectId
    principalType: 'User'
  }
}

// Alert channel. The Function writes "ALERT PR <id>: <reviewer>: <reason>" as a warning log.
// This rule finds those lines every 5 minutes and emails them. One email per line (dimension Message).
resource alertGroup 'Microsoft.Insights/actionGroups@2023-01-01' = if (!empty(alertEmail)) {
  name: 'ag-${appName}'
  location: 'global'
  properties: {
    groupShortName: 'ReviewGuard'
    enabled: true
    emailReceivers: [
      { name: 'cto', emailAddress: alertEmail, useCommonAlertSchema: true }
    ]
  }
}

resource fastApprovalAlert 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = if (!empty(alertEmail)) {
  name: 'alert-${appName}-fast-approval'
  location: location
  properties: {
    displayName: 'Review Guard: large PR approved too fast'
    description: 'A reviewer approved a large PR fast and left no inline comments. See the PR status review-guard/review-depth.'
    severity: 2
    enabled: true
    scopes: [logs.id]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    autoMitigate: false
    criteria: {
      allOf: [
        {
          query: 'AppTraces | where Message startswith "ALERT PR" | project TimeGenerated, Message'
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
          dimensions: [
            { name: 'Message', operator: 'Include', values: ['*'] }
          ]
          failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
        }
      ]
    }
    actions: {
      actionGroups: [alertGroup.id]
    }
  }
}

output functionAppName string = functionApp.name
output functionBaseUrl string = 'https://${functionApp.properties.defaultHostName}'
output keyVaultName string = keyVault.name
output storageAccountName string = storage.name
output functionPrincipalId string = functionApp.identity.principalId
