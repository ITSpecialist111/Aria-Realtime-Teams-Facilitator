targetScope = 'resourceGroup'

@description('Short name used to derive resource names.')
param name string = 'rtbot'

@description('Location for all resources. Realtime models are region-limited.')
param location string = 'swedencentral'

@description('DNS label for the bot public IP. Becomes <label>.<region>.cloudapp.azure.com.')
param dnsLabel string

@description('Administrator username for the Windows media host.')
param adminUsername string

@secure()
@description('Administrator password for the Windows media host.')
param adminPassword string

@description('Object id allowed to manage the Key Vault (the deploying operator).')
param operatorObjectId string

@description('Realtime speech-to-speech model to deploy.')
param realtimeModel string = 'gpt-realtime'

@description('Version of the realtime model.')
param realtimeModelVersion string = '2025-08-28'

@description('Realtime deployment capacity. For realtime models this is requests per minute, not TPM.')
param realtimeCapacity int = 10

@description('Transcription model used for the meeting chat transcript.')
param transcriptionModel string = 'whisper'

@description('Version of the transcription model.')
param transcriptionModelVersion string = '001'

@description('Media host VM size. The media stack needs at least 4 vCPU.')
param vmSize string = 'Standard_D4s_v5'

@description('Public IP address ranges permitted to reach the operator API and RDP.')
param allowedManagementCidrs array = []

var suffix = uniqueString(resourceGroup().id)
var aiServicesName = 'ai-${name}-${suffix}'
var keyVaultName = take('kv-${name}-${suffix}', 24)
var vmName = 'vm-${name}'
var signallingPort = 443
var mediaPort = 8445

// ---------------------------------------------------------------------------
// Observability
// ---------------------------------------------------------------------------

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${name}-${suffix}'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${name}-${suffix}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

// ---------------------------------------------------------------------------
// Identity
// ---------------------------------------------------------------------------

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${name}-${suffix}'
  location: location
}

// ---------------------------------------------------------------------------
// AI Services: hosts both the Voice Live endpoint and the realtime deployments
// ---------------------------------------------------------------------------

resource aiServices 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: aiServicesName
  location: location
  kind: 'AIServices'
  sku: { name: 'S0' }
  identity: { type: 'SystemAssigned' }
  properties: {
    // A custom subdomain is required for Entra ID authentication.
    customSubDomainName: aiServicesName
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: false
  }
}

resource realtimeDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  parent: aiServices
  name: realtimeModel
  sku: {
    name: 'GlobalStandard'
    capacity: realtimeCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: realtimeModel
      version: realtimeModelVersion
    }
  }
}

resource transcriptionDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  parent: aiServices
  name: transcriptionModel
  sku: {
    name: 'Standard'
    capacity: 3
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: transcriptionModel
      version: transcriptionModelVersion
    }
  }
  dependsOn: [ realtimeDeployment ]
}

// ---------------------------------------------------------------------------
// Key Vault: holds the bot client secret and the delegated Work IQ refresh token
// ---------------------------------------------------------------------------

resource keyVault 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: keyVaultName
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    publicNetworkAccess: 'Enabled'
  }
}

// ---------------------------------------------------------------------------
// Network
// ---------------------------------------------------------------------------

resource nsg 'Microsoft.Network/networkSecurityGroups@2024-05-01' = {
  name: 'nsg-${name}'
  location: location
  properties: {
    securityRules: concat(
      [
        {
          // Teams signalling. Source must stay open: the calling service has no fixed egress range.
          name: 'allow-signalling'
          properties: {
            priority: 100
            direction: 'Inbound'
            access: 'Allow'
            protocol: 'Tcp'
            sourceAddressPrefix: 'Internet'
            sourcePortRange: '*'
            destinationAddressPrefix: '*'
            destinationPortRange: '${signallingPort}'
          }
        }
        {
          // Real-time media. TCP, not UDP.
          name: 'allow-media'
          properties: {
            priority: 110
            direction: 'Inbound'
            access: 'Allow'
            protocol: 'Tcp'
            sourceAddressPrefix: 'Internet'
            sourcePortRange: '*'
            destinationAddressPrefix: '*'
            destinationPortRange: '${mediaPort}'
          }
        }
        {
          // Required only for the Let's Encrypt HTTP-01 challenge and its renewals.
          name: 'allow-acme-http'
          properties: {
            priority: 120
            direction: 'Inbound'
            access: 'Allow'
            protocol: 'Tcp'
            sourceAddressPrefix: 'Internet'
            sourcePortRange: '*'
            destinationAddressPrefix: '*'
            destinationPortRange: '80'
          }
        }
      ],
      map(range(0, length(allowedManagementCidrs)), i => {
        name: 'allow-rdp-${i}'
        properties: {
          priority: 200 + i
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: allowedManagementCidrs[i]
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRange: '3389'
        }
      })
    )
  }
}

resource vnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: 'vnet-${name}'
  location: location
  properties: {
    addressSpace: { addressPrefixes: [ '10.20.0.0/16' ] }
    subnets: [
      {
        name: 'media'
        properties: {
          addressPrefix: '10.20.1.0/24'
          networkSecurityGroup: { id: nsg.id }
        }
      }
    ]
  }
}

// A dedicated, statically assigned address. The media platform requires an
// instance-level public IP that maps to exactly this host.
resource publicIp 'Microsoft.Network/publicIPAddresses@2024-05-01' = {
  name: 'pip-${name}'
  location: location
  sku: { name: 'Standard' }
  properties: {
    publicIPAllocationMethod: 'Static'
    dnsSettings: { domainNameLabel: dnsLabel }
  }
}

resource nic 'Microsoft.Network/networkInterfaces@2024-05-01' = {
  name: 'nic-${name}'
  location: location
  properties: {
    ipConfigurations: [
      {
        name: 'ipconfig1'
        properties: {
          subnet: { id: vnet.properties.subnets[0].id }
          privateIPAllocationMethod: 'Dynamic'
          publicIPAddress: { id: publicIp.id }
        }
      }
    ]
  }
}

// ---------------------------------------------------------------------------
// Media host
// ---------------------------------------------------------------------------

resource vm 'Microsoft.Compute/virtualMachines@2024-07-01' = {
  name: vmName
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identity.id}': {} }
  }
  properties: {
    hardwareProfile: { vmSize: vmSize }
    osProfile: {
      computerName: vmName
      adminUsername: adminUsername
      adminPassword: adminPassword
      windowsConfiguration: {
        enableAutomaticUpdates: true
        provisionVMAgent: true
        patchSettings: { patchMode: 'AutomaticByOS' }
      }
    }
    storageProfile: {
      imageReference: {
        publisher: 'MicrosoftWindowsServer'
        offer: 'WindowsServer'
        sku: '2022-datacenter-azure-edition'
        version: 'latest'
      }
      osDisk: {
        createOption: 'FromImage'
        managedDisk: { storageAccountType: 'Premium_LRS' }
        diskSizeGB: 128
      }
    }
    networkProfile: {
      networkInterfaces: [ { id: nic.id } ]
    }
  }
}

// ---------------------------------------------------------------------------
// Role assignments for the bot's managed identity
// ---------------------------------------------------------------------------

var cognitiveServicesUserRoleId = 'a97b65f3-24c7-4388-baec-2e87135dc908'
var keyVaultSecretsOfficerRoleId = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'
var monitoringMetricsPublisherRoleId = '3913510d-42f4-4e42-8a64-420c390055eb'

resource aiRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: aiServices
  name: guid(aiServices.id, identity.id, cognitiveServicesUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesUserRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource vaultRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, identity.id, keyVaultSecretsUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource metricsRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: appInsights
  name: guid(appInsights.id, identity.id, monitoringMetricsPublisherRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', monitoringMetricsPublisherRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource operatorVaultRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(operatorObjectId)) {
  scope: keyVault
  name: guid(keyVault.id, operatorObjectId, keyVaultSecretsOfficerRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsOfficerRoleId)
    principalId: operatorObjectId
  }
}

// ---------------------------------------------------------------------------

output serviceFqdn string = publicIp.properties.dnsSettings.fqdn
output publicIpAddress string = publicIp.properties.ipAddress
output aiServicesEndpoint string = aiServices.properties.endpoint
output aiServicesName string = aiServices.name
output realtimeDeploymentName string = realtimeDeployment.name
output transcriptionDeploymentName string = transcriptionDeployment.name
output keyVaultUri string = keyVault.properties.vaultUri
output keyVaultName string = keyVault.name
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output managedIdentityClientId string = identity.properties.clientId
output managedIdentityPrincipalId string = identity.properties.principalId
output vmName string = vm.name
