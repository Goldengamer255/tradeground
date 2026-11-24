// Tradeground's hosting on Azure Container Apps. The deploy job in .github/workflows/ci.yml applies
// this file on every push to main, so what runs in Azure always matches what is written here.
//
//   az deployment group create --resource-group tradeground --template-file infra/main.bicep \
//     --parameters image=ghcr.io/goldengamer255/tradeground:<commit sha>

@description('The container image to run, tagged with the commit it was built from.')
param image string

@description('Defaults to the resource group\'s region (New Zealand North, the closest to Auckland).')
param location string = resourceGroup().location

param name string = 'tradeground'

// The environment is the shared space container apps run in (networking, scaling).
// No Log Analytics workspace is attached: it bills per GB of logs, and the free plan should stay free.
resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${name}-env'
  location: location
  properties: {}
}

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: name
  location: location
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      // Each deploy replaces the running version instead of running old and new side by side.
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        // 'auto' allows HTTP/1.1 upgrades, which SignalR's WebSockets need.
        transport: 'auto'
        // Azure provides HTTPS; plain HTTP requests are redirected to it.
        allowInsecure: false
      }
    }
    template: {
      containers: [
        {
          name: 'web'
          image: image
          // The smallest size Container Apps offers; plenty for a game server.
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          probes: [
            {
              type: 'Readiness'
              httpGet: { path: '/health', port: 8080 }
              periodSeconds: 10
            }
            {
              type: 'Liveness'
              httpGet: { path: '/health', port: 8080 }
              initialDelaySeconds: 10
              periodSeconds: 30
            }
          ]
        }
      ]
      scale: {
        // Zero when nobody is playing, so idle time costs nothing (the first visitor waits a few seconds).
        minReplicas: 0
        // Never more than one: rooms live in this one server's memory, so a second copy would have
        // different rooms and players would land in the wrong one.
        maxReplicas: 1
      }
    }
  }
}

output url string = 'https://${app.properties.configuration.ingress.fqdn}'
