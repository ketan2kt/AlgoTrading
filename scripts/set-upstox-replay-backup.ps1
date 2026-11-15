[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)][switch]$Activate)

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
if (-not $PSCmdlet.ShouldProcess($recorder, 'Configure the read-only Upstox backup token; the recorder may restart')) { return }
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
    throw 'Recorder-only Upstox configuration failed or could not be confirmed. No credential values were logged.'
} finally {
    $credential.Dispose(); $value = $null; $azureToken = $null; $headers = $null
    $settings = $null; $settingsResponse = $null; $saved = $null
}
