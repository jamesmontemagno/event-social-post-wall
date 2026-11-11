<#
.SYNOPSIS
Runs separate wall, submission, and admin deployments with simulated social sources.
#>
[CmdletBinding()]
param(
    [int]$Port = 5080,
    [string]$StaffPasscode = '',
    [int]$IntervalSeconds = 7,
    [switch]$Reset,
    [switch]$SkipWebBuild
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$web = Join-Path $root 'src\EventWall.Web'
$api = Join-Path $root 'src\EventWall.Api'
$dataDirectory = Join-Path $api 'App_Data'
$database = Join-Path $dataDirectory 'eventwall-demo.db'
$adminPort = $Port + 1
$submissionPort = $Port + 2
if (-not $StaffPasscode) { $StaffPasscode = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(24)) }
if ($StaffPasscode.Length -lt 12) { throw 'StaffPasscode must be at least 12 characters.' }
New-Item -ItemType Directory -Force -Path $dataDirectory | Out-Null

foreach ($role in @('public', 'admin')) {
    $assets = Join-Path $dataDirectory "demo-$role-wwwroot"
    if (-not $SkipWebBuild) {
        $env:VITE_SITE_ROLE = $role
        Remove-Item Env:VITE_API_BASE_URL -ErrorAction SilentlyContinue
        npm run build --prefix $web
        if ($LASTEXITCODE -ne 0) { throw "$role web build failed. Run npm ci --prefix src\EventWall.Web if dependencies are missing." }
        if (Test-Path $assets) { Remove-Item -LiteralPath $assets -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $assets | Out-Null
        Copy-Item (Join-Path $web 'dist\*') $assets -Recurse -Force
    }
    if (-not (Test-Path (Join-Path $assets 'index.html'))) { throw "Missing $role assets; run without -SkipWebBuild." }
}
Remove-Item Env:VITE_SITE_ROLE -ErrorAction SilentlyContinue

dotnet build (Join-Path $api 'EventWall.Api.csproj') --nologo
if ($LASTEXITCODE -ne 0) { throw 'API build failed.' }
if ($Reset) {
    foreach ($suffix in @('', '-shm', '-wal')) {
        Remove-Item -LiteralPath "$database$suffix" -Force -ErrorAction SilentlyContinue
    }
    Write-Host 'Demo database reset.'
}

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Database__Provider = 'Sqlite'
$env:ConnectionStrings__EventWall = "Data Source=$database"
$env:Demo__Enabled = 'true'
$env:Demo__IntervalSeconds = "$IntervalSeconds"
$env:Event__SubmissionsInitiallyOpen = 'true'
$dll = Join-Path $api 'bin\Debug\net10.0\EventWall.Api.dll'
$processes = @()
try {
    foreach ($role in @('admin', 'public', 'submission')) {
        $sitePort = if ($role -eq 'admin') { $adminPort } elseif ($role -eq 'submission') { $submissionPort } else { $Port }
        $env:Deployment__Role = $role
        $env:ASPNETCORE_WEBROOT = if ($role -eq 'submission') { Join-Path $api 'wwwroot' } else { Join-Path $dataDirectory "demo-$role-wwwroot" }
        $env:Public__SubmissionSiteUrl = "http://127.0.0.1:$submissionPort"
        if ($role -eq 'admin') {
            $env:STAFF_PASSCODE = $StaffPasscode
            $env:Public__WallSiteUrl = "http://127.0.0.1:$Port"
        }
        else {
            Remove-Item Env:STAFF_PASSCODE -ErrorAction SilentlyContinue
            Remove-Item Env:Public__WallSiteUrl -ErrorAction SilentlyContinue
        }
        $stdout = Join-Path $dataDirectory "demo-$role.out.log"
        $stderr = Join-Path $dataDirectory "demo-$role.err.log"
        $process = Start-Process dotnet -ArgumentList "`"$dll`" --urls http://127.0.0.1:$sitePort" -WorkingDirectory $api -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        $processes += $process
        $ready = $false
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            if ($process.HasExited) {
                Get-Content $stdout, $stderr -Tail 25
                throw "$role deployment exited with code $($process.ExitCode)."
            }
            try {
                $null = Invoke-RestMethod "http://127.0.0.1:$sitePort/api/health" -TimeoutSec 2
                $ready = $true
                break
            }
            catch { Start-Sleep -Milliseconds 500 }
        }
        if (-not $ready) { throw "$role deployment did not become ready. See $stdout and $stderr." }
    }
    Write-Host "Public wall:       http://127.0.0.1:$Port" -ForegroundColor Green
    Write-Host "Questions:         http://127.0.0.1:$submissionPort"
    Write-Host "Private admin:     http://127.0.0.1:$adminPort  (demo passcode: $StaffPasscode)"
    Write-Host 'Loopback-only demo. Simulated sources, no provider calls. Ctrl+C stops all three deployments.'
    while ($true) {
        foreach ($process in $processes) {
            if ($process.HasExited) { throw "Demo process $($process.Id) exited. Check the App_Data demo logs." }
        }
        Start-Sleep -Seconds 1
    }
}
finally {
    foreach ($process in $processes) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction Continue }
    }
}
