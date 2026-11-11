@description('Azure region for the event wall resources.')
param location string = resourceGroup().location

@description('A globally unique prefix for the web app and SQL server.')
@minLength(3)
@maxLength(20)
param appName string

@description('SQL administrator login. Prefer a dedicated deployment-only account.')
param sqlAdministratorLogin string

@secure()
@description('SQL administrator password. Never commit this value.')
param sqlAdministratorPassword string

@secure()
@description('Production secrets: staffPasscode, publicSqlPassword, submissionSqlPassword, adminSqlPassword. Use distinct random values. Pass {} only with stagingOnly=true.')
param productionSecrets object

@secure()
@description('Staging secrets with the same keys as productionSecrets, but different values. Pass {} only when staging is not provisioned.')
param stagingSecrets object

@description('Single-event database identifier; do not change this on an existing event database.')
param eventSlug string = 'communityevent'

@description('Event display name and literal social search phrase.')
@minLength(1)
@maxLength(160)
param eventName string = 'Community Event'

@description('Social hashtag, including #. Used by X, Bluesky, and Mastodon collectors.')
param eventHashtag string = '#communityevent'

@description('App Service size. Keep one instance until distributed state and collector ownership are implemented.')
@allowed(['B1', 'B2', 'B3', 'S1', 'S2', 'S3', 'P0v3', 'P1v3', 'P2v3'])
param appServiceSku string = 'B1'

@description('Optional custom HTTPS URLs. Empty values use the generated App Service hostnames.')
param productionWallUrl string = ''
param productionSubmissionUrl string = ''
param stagingWallUrl string = ''
param stagingSubmissionUrl string = ''

@description('Create separate staging web/database resources. Additional SQL charges apply.')
param includeStaging bool = true

@description('Provision only the demo web apps; production web apps remain absent.')
param stagingOnly bool = false

@description('Provision the empty production database during a staging-only deployment. Set false only to defer it.')
param includeProductionDatabase bool = true

@description('Use simulated sources in staging only. Production is never simulated by this template.')
param demoEnabled bool = false

@description('SQL client IPv4 addresses allowed through the database firewall, including app outbound IPs. Empty denies all clients.')
param sqlAllowedIpAddresses array = []

@description('Explicit origins allowed to call the API from a static host.')
param corsOrigins array = []

@description('Optional Cloudflare Turnstile site key for the question sites. Leave empty to disable the challenge.')
param turnstileSiteKey string = ''

@secure()
@description('Cloudflare Turnstile secret key; required when turnstileSiteKey is set. Never commit this value.')
param turnstileSecretKey string = ''

var appServicePlanName = '${appName}-plan'
var webAppName = '${appName}-app'
var sqlServerName = '${appName}-sql'
var productionDatabaseName = 'EventWallProd'
var stagingAppName = '${appName}-stage'
var demoDatabaseName = 'EventWallStaging'
var adminAppName = '${appName}-admin'
var stagingAdminAppName = '${appName}-admin-stage'
var submissionAppName = '${appName}-submit'
var stagingSubmissionAppName = '${appName}-submit-stage'

resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: appServicePlanName
  location: location
  kind: 'linux'
  sku: {
    name: appServiceSku
    capacity: 1
  }
  properties: {
    reserved: true
  }
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01' = {
  name: sqlServerName
  location: location
  properties: {
    administratorLogin: sqlAdministratorLogin
    administratorLoginPassword: sqlAdministratorPassword
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

resource productionDatabase 'Microsoft.Sql/servers/databases@2023-08-01' = if (!stagingOnly || includeProductionDatabase) {
  parent: sqlServer
  name: productionDatabaseName
  location: location
  sku: {
    name: 'Basic'
    tier: 'Basic'
    capacity: 5
  }
  properties: {
    requestedBackupStorageRedundancy: 'Local'
    zoneRedundant: false
  }
}

resource demoDatabase 'Microsoft.Sql/servers/databases@2023-08-01' = if (includeStaging || stagingOnly) {
  parent: sqlServer
  name: demoDatabaseName
  location: location
  sku: {
    name: 'Basic'
    tier: 'Basic'
    capacity: 5
  }
  properties: {
    requestedBackupStorageRedundancy: 'Local'
    zoneRedundant: false
  }
}

resource sqlFirewallRules 'Microsoft.Sql/servers/firewallRules@2023-08-01' = [for (address, index) in sqlAllowedIpAddresses: {
  parent: sqlServer
  name: 'AllowedClient-${index}'
  properties: {
    startIpAddress: address
    endIpAddress: address
  }
}]

var sharedAppSettings = [
  {
    name: 'Event__Slug'
    value: eventSlug
  }
  {
    name: 'Event__Name'
    value: eventName
  }
  {
    name: 'Event__QuestionHashtag'
    value: eventHashtag
  }
  {
    name: 'Database__Provider'
    value: 'SqlServer'
  }
  {
    name: 'Database__ApplyMigrations'
    value: 'false'
  }
  {
    name: 'WEBSITES_ENABLE_APP_SERVICE_STORAGE'
    value: 'true'
  }
]

var corsAppSettings = [for (origin, index) in corsOrigins: {
  name: 'Public__CorsOrigins__${index}'
  value: origin
}]

var sites = concat(stagingOnly ? [] : [
  { name: webAppName, role: 'Public', staging: false, database: productionDatabaseName, databasePurpose: 'Production' }
  { name: adminAppName, role: 'Admin', staging: false, database: productionDatabaseName, databasePurpose: 'Production' }
  { name: submissionAppName, role: 'Submission', staging: false, database: productionDatabaseName, databasePurpose: 'Production' }
], (includeStaging || stagingOnly) ? [
  { name: stagingAppName, role: 'Public', staging: true, database: demoDatabaseName, databasePurpose: 'Demo' }
  { name: stagingAdminAppName, role: 'Admin', staging: true, database: demoDatabaseName, databasePurpose: 'Demo' }
  { name: stagingSubmissionAppName, role: 'Submission', staging: true, database: demoDatabaseName, databasePurpose: 'Demo' }
] : [])

resource webApps 'Microsoft.Web/sites@2023-12-01' = [for site in sites: {
  name: site.name
  location: location
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    clientAffinityEnabled: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: true
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      http20Enabled: true
      webSocketsEnabled: site.role == 'Submission'
      ipSecurityRestrictionsDefaultAction: 'Allow'
      ipSecurityRestrictions: []
      // SCM remains independently authenticated by Azure/OIDC.
      scmIpSecurityRestrictionsUseMain: false
      scmIpSecurityRestrictionsDefaultAction: 'Allow'
      appSettings: concat(sharedAppSettings, [
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: site.staging ? 'Staging' : 'Production'
        }
        {
          name: 'Deployment__Role'
          value: site.role
        }
        {
          name: 'Database__Purpose'
          value: site.databasePurpose
        }
        {
          name: 'Demo__Enabled'
          value: string(site.staging && demoEnabled)
        }
        {
          name: 'Event__SubmissionsInitiallyOpen'
          value: 'false'
        }
      ], site.role == 'Admin' ? [
        {
          name: 'STAFF_PASSCODE'
          value: site.staging ? stagingSecrets.staffPasscode : productionSecrets.staffPasscode
        }
        {
          name: 'DATA_PROTECTION_KEY_PATH'
          value: '/home/data/eventwall-keys'
        }
        {
          name: 'Public__WallSiteUrl'
          value: site.staging
            ? (empty(stagingWallUrl) ? 'https://${stagingAppName}.azurewebsites.net' : stagingWallUrl)
            : (empty(productionWallUrl) ? 'https://${webAppName}.azurewebsites.net' : productionWallUrl)
        }
        {
          name: 'Public__SubmissionSiteUrl'
          value: site.staging
            ? (empty(stagingSubmissionUrl) ? 'https://${stagingSubmissionAppName}.azurewebsites.net' : stagingSubmissionUrl)
            : (empty(productionSubmissionUrl) ? 'https://${submissionAppName}.azurewebsites.net' : productionSubmissionUrl)
        }
      ] : site.role == 'Submission' ? concat([
        {
          name: 'DATA_PROTECTION_KEY_PATH'
          value: '/home/data/eventwall-submission-keys'
        }
      ], empty(turnstileSiteKey) ? [] : [
        {
          name: 'Turnstile__SiteKey'
          value: turnstileSiteKey
        }
        {
          name: 'Turnstile__SecretKey'
          value: turnstileSecretKey
        }
        {
          name: 'Turnstile__Required'
          value: 'true'
        }
      ]) : concat(corsAppSettings, [
        {
          name: 'Public__SubmissionSiteUrl'
          value: site.staging
            ? (empty(stagingSubmissionUrl) ? 'https://${stagingSubmissionAppName}.azurewebsites.net' : stagingSubmissionUrl)
            : (empty(productionSubmissionUrl) ? 'https://${submissionAppName}.azurewebsites.net' : productionSubmissionUrl)
        }
      ]))
      connectionStrings: [
        {
          name: 'EventWall'
          connectionString: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Initial Catalog=${site.database};Persist Security Info=False;User ID=eventwall_${toLower(site.role)};Password="${replace(site.staging ? stagingSecrets['${toLower(site.role)}SqlPassword'] : productionSecrets['${toLower(site.role)}SqlPassword'], '"', '""')}";MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
          type: 'SQLAzure'
        }
      ]
    }
  }
  dependsOn: [
    productionDatabase
    demoDatabase
  ]
}]

output appUrl string = stagingOnly ? '' : 'https://${webAppName}.azurewebsites.net'
output adminUrl string = stagingOnly ? '' : 'https://${adminAppName}.azurewebsites.net'
output stagingUrl string = (includeStaging || stagingOnly) ? 'https://${stagingAppName}.azurewebsites.net' : ''
output stagingAdminUrl string = (includeStaging || stagingOnly) ? 'https://${stagingAdminAppName}.azurewebsites.net' : ''
output submissionUrl string = stagingOnly ? '' : 'https://${submissionAppName}.azurewebsites.net'
output stagingSubmissionUrl string = (includeStaging || stagingOnly) ? 'https://${stagingSubmissionAppName}.azurewebsites.net' : ''
output sqlServerHost string = sqlServer.properties.fullyQualifiedDomainName
