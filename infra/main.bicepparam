using './main.bicep'

param appName = readEnvironmentVariable('EVENTWALL_APP_NAME')
param sqlAdministratorLogin = readEnvironmentVariable('SQL_ADMIN_LOGIN')
param sqlAdministratorPassword = readEnvironmentVariable('SQL_ADMIN_PASSWORD')
param eventSlug = readEnvironmentVariable('EVENT_SLUG', 'communityevent')
param eventName = readEnvironmentVariable('EVENT_NAME', 'Community Event')
param eventHashtag = readEnvironmentVariable('EVENT_HASHTAG', '#communityevent')
param includeStaging = true
param productionSecrets = {
  staffPasscode: readEnvironmentVariable('PRODUCTION_STAFF_PASSCODE')
  publicSqlPassword: readEnvironmentVariable('PRODUCTION_PUBLIC_SQL_PASSWORD')
  submissionSqlPassword: readEnvironmentVariable('PRODUCTION_SUBMISSION_SQL_PASSWORD')
  adminSqlPassword: readEnvironmentVariable('PRODUCTION_ADMIN_SQL_PASSWORD')
}
param stagingSecrets = {
  staffPasscode: readEnvironmentVariable('STAGING_STAFF_PASSCODE')
  publicSqlPassword: readEnvironmentVariable('STAGING_PUBLIC_SQL_PASSWORD')
  submissionSqlPassword: readEnvironmentVariable('STAGING_SUBMISSION_SQL_PASSWORD')
  adminSqlPassword: readEnvironmentVariable('STAGING_ADMIN_SQL_PASSWORD')
}
param sqlAllowedIpAddresses = json(readEnvironmentVariable('EVENTWALL_SQL_IPS', '[]'))
