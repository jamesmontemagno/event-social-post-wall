[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [string]$ResourceGroup,
    [Parameter(Mandatory)]
    [ValidatePattern('-admin-stage$')]
    [string]$AdminWebApp,
    [Parameter(Mandatory)]
    [ValidatePattern('(?<!admin)-stage$')]
    [string]$PublicWebApp,
    [Parameter(Mandatory)]
    [ValidatePattern('-submit-stage$')]
    [string]$SubmissionWebApp
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($env:EVENTWALL_DEPLOYMENT_SQL)) {
    throw 'Set EVENTWALL_DEPLOYMENT_SQL in this process before resetting demo data.'
}

$requiredVariables = @(
    'EVENTWALL_PUBLIC_SQL_PASSWORD',
    'EVENTWALL_SUBMISSION_SQL_PASSWORD',
    'EVENTWALL_ADMIN_SQL_PASSWORD'
)
foreach ($name in $requiredVariables) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
        throw "Set $name in this process before resetting demo data."
    }
}

$databaseSetupProject = Join-Path $PSScriptRoot '..\EventWall.DatabaseSetup\EventWall.DatabaseSetup.csproj'

function Assert-DemoConnectionString([string]$ConnectionString, [string]$Target) {
    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        throw "No connection string was available for $Target. No apps were stopped."
    }

    $previousConnectionString = $env:EVENTWALL_DEPLOYMENT_SQL
    try {
        $env:EVENTWALL_DEPLOYMENT_SQL = $ConnectionString
        & dotnet run --project $databaseSetupProject --configuration Release -- --validate-demo-target | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "$Target does not point to the demo database. No apps were stopped."
        }
    } finally {
        $env:EVENTWALL_DEPLOYMENT_SQL = $previousConnectionString
    }
}

Assert-DemoConnectionString $env:EVENTWALL_DEPLOYMENT_SQL 'The deployment connection'
$databaseName = 'EventWallStaging'
foreach ($app in @($AdminWebApp, $PublicWebApp, $SubmissionWebApp)) {
    $connectionStrings = & az webapp config connection-string list --resource-group $ResourceGroup --name $app --output json
    if ($LASTEXITCODE -ne 0) {
        throw "Could not read the connection string for staging app '$app'. No apps were stopped."
    }
    $eventWallConnection = @($connectionStrings | ConvertFrom-Json | Where-Object name -eq 'EventWall')
    if ($eventWallConnection.Count -ne 1) {
        throw "Staging app '$app' must have exactly one EventWall connection string. No apps were stopped."
    }
    Assert-DemoConnectionString ([string]$eventWallConnection[0].value) "$app connection"
}

if (-not $PSCmdlet.ShouldProcess(
        "$ResourceGroup/$AdminWebApp, $ResourceGroup/$PublicWebApp, $ResourceGroup/$SubmissionWebApp and database $databaseName",
        'Stop staging apps, clear and reseed demo data, then restore their prior running state')) {
    return
}

function Get-WebAppState([string]$Name) {
    $state = & az webapp show --resource-group $ResourceGroup --name $Name --query state --output tsv
    if ($LASTEXITCODE -ne 0) {
        throw "Could not read the state of staging app '$Name'."
    }
    return "$state".Trim()
}

function Invoke-Az([string[]]$Arguments) {
    & az @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI operation failed: az $($Arguments -join ' ')"
    }
}

$apps = @($AdminWebApp, $PublicWebApp, $SubmissionWebApp)
$runningApps = @()
foreach ($app in $apps) {
    $state = Get-WebAppState $app
    if ($state -eq 'Running') {
        $runningApps += $app
    } elseif ($state -ne 'Stopped') {
        throw "Staging app '$app' is in unexpected state '$state'; no apps were stopped."
    }
}

$stoppedApps = @()
$operationError = $null
try {
    foreach ($app in $runningApps) {
        Invoke-Az -Arguments @('webapp', 'stop', '--resource-group', $ResourceGroup, '--name', $app, '--output', 'none')
        $stoppedApps += $app
    }

    & dotnet run --project $databaseSetupProject --configuration Release -- --reset-demo --confirm-demo-reset
    if ($LASTEXITCODE -ne 0) {
        throw 'Demo database reset failed.'
    }
} catch {
    $operationError = $_
}

$restartErrors = @()
foreach ($app in $stoppedApps) {
    try {
        Invoke-Az -Arguments @('webapp', 'start', '--resource-group', $ResourceGroup, '--name', $app, '--output', 'none')
    } catch {
        $restartErrors += $app
        Write-Warning "Could not restart staging app '$app'. Start it from Azure before reopening the demo."
    }
}

if ($operationError) {
    if ($restartErrors.Count -gt 0) {
        Write-Warning "Apps needing manual restart: $($restartErrors -join ', ')."
    }
    throw $operationError
}
if ($restartErrors.Count -gt 0) {
    throw "Demo data was reset, but these apps need manual restart: $($restartErrors -join ', ')."
}

Write-Host 'Demo database reset and fictional fixtures reseeded. Production was not touched.'
