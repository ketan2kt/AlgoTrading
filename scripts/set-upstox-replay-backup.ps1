[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)][switch]$Activate, [switch]$IndependentWebsite,
    [ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedInfrastructureHash)

$ErrorActionPreference = 'Stop'
if (-not $Activate -or -not $IsWindows) { throw 'Use the explicitly authorized Windows activation for this recorder only.' }
$indiaNow = [DateTimeOffset]::UtcNow.ToOffset([TimeSpan]::FromMinutes(330))
if ($indiaNow.DayOfWeek -notin @('Saturday','Sunday') -and $indiaNow.Hour -ge 8 -and $indiaNow.Hour -lt 16) {
    throw 'Upstox configuration would restart the recorder. Wait until capture ends.'
}
$credentialFile = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Sarthico/Upstox/analytics-token.dpapi'
if (-not (Test-Path -LiteralPath $credentialFile -PathType Leaf)) { throw 'The approved Windows-encrypted Upstox token is missing.' }
$recorder = 'sarthirecorder5amvyarepxaci'
$group = 'rg-sarthi-trading-centralindia'
$subscription = '8f1fb990-d44b-4dd2-b8f4-4208cc40c340'
$address = "https://management.azure.com/subscriptions/$subscription/resourceGroups/$group/providers/Microsoft.Web/sites/$recorder/config/appsettings"
$setting = 'ReplayBackup__UpstoxAnalyticsToken'
$version = '2024-11-01'
if (-not $PSCmdlet.ShouldProcess($(if ($IndependentWebsite) { 'Website backup and recorder ownership' } else { $recorder }), 'Configure read-only Upstox backup; affected hosts may restart')) { return }
$credential = ConvertTo-SecureString ([IO.File]::ReadAllText($credentialFile))
try {
    $azureToken = az account get-access-token --subscription $subscription --resource https://management.azure.com/ --query accessToken -o tsv
    if ($LASTEXITCODE -ne 0 -or -not $azureToken) { throw 'Azure management authentication failed.' }
    $headers = @{ Authorization = "Bearer $azureToken" }
    $settingsResponse = Invoke-RestMethod -Method Post -Uri "$address/list?api-version=$version" -Headers $headers -TimeoutSec 30
    if ($settingsResponse.properties.ApplicationRole -ne 'ReplayRecorder') { throw 'Refusing to configure a non-recorder host.' }
    $settings = @{}
    foreach ($property in $settingsResponse.properties.PSObject.Properties) { $settings[$property.Name] = $property.Value }
    $value = [Net.NetworkCredential]::new('', $credential).Password
    if ($IndependentWebsite) {
        if (-not $ExpectedInfrastructureHash) { throw 'Supply the checked Infrastructure assembly hash before transferring backup ownership.' }
        $website = 'sarthitradingweb5amvyarepxaci'
        foreach ($site in @($recorder, $website)) {
            $package = $null
            for ($attempt = 0; $attempt -lt 5; $attempt++) {
                try {
                    $package = Invoke-WebRequest -Uri "https://$site.scm.azurewebsites.net/api/vfs/site/wwwroot/TradingSystem.Infrastructure.dll" -Headers $headers -TimeoutSec 20
                    break
                } catch {
                    if ($attempt -eq 4) { throw 'Package identity is unavailable; no settings changed.' }
                    Start-Sleep -Seconds 5
                }
            }
            if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$package.Content)) -ne $ExpectedInfrastructureHash) {
                throw 'Both hosts must run the checked independent-backup package before ownership changes.'
            }
        }
        $webAddress = $address.Replace("/sites/$recorder/", "/sites/$website/")
        $webResponse = Invoke-RestMethod -Method Post -Uri "$webAddress/list?api-version=$version" -Headers $headers -TimeoutSec 30
        if ($webResponse.properties.ApplicationRole -eq 'ReplayRecorder') { throw 'Website role is invalid.' }
        $web = @{}
        foreach ($property in $webResponse.properties.PSObject.Properties) { $web[$property.Name] = $property.Value }
        # Stop the old backup owner first, leaving primary Groww capture enabled.
        # Both checked packages must support this ownership setting before use.
        $settings['ReplayBackup__Host'] = 'Website'
        $settings[$setting] = $value # Also used by after-hours candle repair.
        Invoke-RestMethod -Method Put -Uri "${address}?api-version=$version" -Headers $headers -ContentType 'application/json' `
            -Body (@{ properties = $settings } | ConvertTo-Json -Compress -Depth 4) -TimeoutSec 30 | Out-Null
        $confirmed = Invoke-RestMethod -Method Post -Uri "$address/list?api-version=$version" -Headers $headers -TimeoutSec 30
        if ($confirmed.properties.ReplayBackup__Host -ne 'Website') { throw 'Backup ownership change was not confirmed.' }
        $web[$setting] = $value
        $web['ReplayBackup__Host'] = 'Website'
        Invoke-RestMethod -Method Put -Uri "${webAddress}?api-version=$version" -Headers $headers -ContentType 'application/json' `
            -Body (@{ properties = $web } | ConvertTo-Json -Compress -Depth 4) -TimeoutSec 30 | Out-Null
        $saved = Invoke-RestMethod -Method Post -Uri "$webAddress/list?api-version=$version" -Headers $headers -TimeoutSec 30
        if ($saved.properties.$setting -cne $value -or $saved.properties.ReplayBackup__Host -ne 'Website') { throw 'Website backup was not confirmed.' }
        Write-Host 'Upstox backup assigned to website; recorder retains Groww capture and after-hours candle repair. No order settings changed.'
        return
    }
    if ($settings[$setting] -ceq $value) { Write-Host 'Upstox is already configured on the recorder; no restart requested.'; return }
    $settings[$setting] = $value
    # Never send the token via a native command argument, Git, diagnostics, or the website app.
    Invoke-RestMethod -Method Put -Uri "${address}?api-version=$version" -Headers $headers -ContentType 'application/json' `
        -Body (@{ properties = $settings } | ConvertTo-Json -Compress -Depth 4) -TimeoutSec 30 | Out-Null
    $saved = Invoke-RestMethod -Method Post -Uri "$address/list?api-version=$version" -Headers $headers -TimeoutSec 30
    if ($saved.properties.$setting -cne $value) { throw 'The recorder token update was not confirmed.' }
    Write-Host 'Read-only Upstox backup configured on the recorder only. Live market capture is not yet verified.'
} catch {
    # Do not propagate errors that may include returned App Service settings.
    throw 'Upstox configuration failed or could not be confirmed. No credential values were logged. Verify backup ownership before retrying.'
} finally {
    $credential.Dispose(); $value = $null; $azureToken = $null; $headers = $null
    $settings = $null; $settingsResponse = $null; $saved = $null
    $web = $null; $webResponse = $null; $confirmed = $null
}
