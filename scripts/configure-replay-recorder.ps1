[CmdletBinding()]
param([switch]$Activate, [ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedAssemblyHash,
    [switch]$AllowMarketHoursRestart)
$ErrorActionPreference = 'Stop'
$rg = 'rg-sarthi-trading-centralindia'
$website = 'sarthitradingweb5amvyarepxaci'
$recorder = 'sarthirecorder5amvyarepxaci'
$subscription = '8f1fb990-d44b-4dd2-b8f4-4208cc40c340'
$baseUri = "https://management.azure.com/subscriptions/$subscription/resourceGroups/$rg/providers/Microsoft.Web/sites"
$local = [DateTimeOffset]::UtcNow.ToOffset([TimeSpan]::FromHours(5.5))
if ($Activate -and -not $AllowMarketHoursRestart -and $local.DayOfWeek -notin @('Saturday','Sunday') -and $local.Hour -ge 8 -and $local.Hour -lt 16) {
    throw 'Recorder activation/restart is restricted to outside the capture/trading window.'
}
$managementToken = az account get-access-token --resource https://management.azure.com/ --query accessToken -o tsv
if ($LASTEXITCODE -ne 0) { throw 'Azure authentication failed' }
$headers = @{ Authorization = "Bearer $managementToken" }
try {
    if ($ExpectedAssemblyHash) {
        $verified = Invoke-RestMethod -Method Post -Uri "https://$website.scm.azurewebsites.net/api/command" -Headers $headers `
            -ContentType 'application/json' -Body '{"command":"sha256sum /home/site/wwwroot/TradingSystem.Application.dll","dir":"/home/site/wwwroot"}'
        if ($verified.ExitCode -ne 0 -or ($verified.Output -split '\s+')[0] -ne $ExpectedAssemblyHash) {
            throw 'Website policy assembly does not match the checked recorder release; deploy the website package first.'
        }
    }
    # Read/write settings only in memory. Never log settings, tokens or bodies.
    $webSettings = Invoke-RestMethod -Method Post -Uri "$baseUri/$website/config/appsettings/list?api-version=2024-11-01" -Headers $headers
    $recSettings = Invoke-RestMethod -Method Post -Uri "$baseUri/$recorder/config/appsettings/list?api-version=2024-11-01" -Headers $headers
    $web = @{}; $rec = @{}
    foreach ($property in $webSettings.properties.PSObject.Properties) { $web[$property.Name] = $property.Value }
    foreach ($property in $recSettings.properties.PSObject.Properties) { $rec[$property.Name] = $property.Value }
    if ([string]::IsNullOrWhiteSpace($web['ConnectionStrings__TradingDatabase'])) { throw 'Database configuration missing' }
    $rec['ConnectionStrings__TradingDatabase'] = $web['ConnectionStrings__TradingDatabase']
    $rec['ApplicationRole'] = 'ReplayRecorder'
    $rec['AllowedHosts'] = "$recorder.azurewebsites.net;localhost"
    $rec['DatabaseInitialization__ApplyMigrations'] = 'false'
    $rec['IdentityBootstrap__Enabled'] = 'false'
    $rec['LiveExecution__BuildEnabled'] = 'false'
    # The Linux B1 host can spend over a minute updating certificates before
    # starting .NET. Allow startup to finish before activation is rolled back.
    $rec['WEBSITES_CONTAINER_START_TIME_LIMIT'] = '600'
    $rec['MarketData__HistoricalReplayFeed__Enabled'] = $(if ($Activate) { 'true' } else { 'false' })
    $rec['MarketData__HistoricalReplayFeed__SampleSeconds'] = '2'
    foreach ($settings in @($web, $rec)) {
        $settings['ReplayStorage__ContainerUri'] = 'https://sarthireplay2026.blob.core.windows.net/replay'
        $settings['ReplayStorage__KeyRingUri'] = 'https://sarthireplay2026.blob.core.windows.net/protected-keys/keyring.xml'
    }
    if ($Activate) {
        $web['MarketData__HistoricalReplayFeed__Enabled'] = 'false'
        $web['MarketData__HistoricalReplayFeed__AfterMarketBackfillEnabled'] = 'false'
    }
    # Website disables its recorder before the independent recorder is enabled.
    Invoke-RestMethod -Method Put -Uri "$baseUri/$website/config/appsettings?api-version=2024-11-01" -Headers $headers `
        -ContentType 'application/json' -Body (@{ properties = $web } | ConvertTo-Json -Depth 6 -Compress) | Out-Null
    try {
        Invoke-RestMethod -Method Put -Uri "$baseUri/$recorder/config/appsettings?api-version=2024-11-01" -Headers $headers `
            -ContentType 'application/json' -Body (@{ properties = $rec } | ConvertTo-Json -Depth 6 -Compress) | Out-Null
        if ($Activate) {
            $active = $false
            for ($attempt = 0; $attempt -lt 120; $attempt++) {
                try {
                    $response = Invoke-WebRequest -Uri "https://$recorder.azurewebsites.net/health/recorder-active" -TimeoutSec 10
                    if ($response.StatusCode -eq 200) { $active = $true; break }
                } catch { } # Deliberately omit HTTP bodies/settings from logs.
                Start-Sleep -Seconds 5
            }
            if (-not $active) { throw 'Recorder did not become healthy with capture enabled.' }
        }
    } catch {
        if ($Activate) {
            # Fail safe: confirm recorder disabled BEFORE restoring the website
            # producer. Never knowingly run competing feeds after failed cutover.
            $rec['MarketData__HistoricalReplayFeed__Enabled'] = 'false'
            Invoke-RestMethod -Method Put -Uri "$baseUri/$recorder/config/appsettings?api-version=2024-11-01" -Headers $headers `
                -ContentType 'application/json' -Body (@{ properties = $rec } | ConvertTo-Json -Depth 6 -Compress) | Out-Null
            $web['MarketData__HistoricalReplayFeed__Enabled'] = 'true'
            $web['MarketData__HistoricalReplayFeed__AfterMarketBackfillEnabled'] = 'true'
            Invoke-RestMethod -Method Put -Uri "$baseUri/$website/config/appsettings?api-version=2024-11-01" -Headers $headers `
                -ContentType 'application/json' -Body (@{ properties = $web } | ConvertTo-Json -Depth 6 -Compress) | Out-Null
            throw 'Recorder cutover failed; recorder disabled and website capture restored. Deployment is NOT complete.'
        }
        throw
    }
    Write-Host $(if ($Activate) { 'Recorder activated; website capture disabled. Verify recorder health before declaring ready.' } else { 'Recorder configuration staged; capture remains disabled until checked packages are deployed.' })
} finally {
    $managementToken = $null; $headers = $null; $webSettings = $null; $recSettings = $null; $web = $null; $rec = $null
}
