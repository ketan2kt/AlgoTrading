[CmdletBinding(SupportsShouldProcess)]
param([switch]$Capture, [switch]$Activate, [string]$AccountId,
    [ValidatePattern('^\d{1,3}(\.\d{1,3}){3}$')][string]$VerifiedEgressIp,
    [ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedInfrastructureHash)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This setup uses Windows user-bound encrypted storage.' }
$store = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Sarthico/Upstox/execution-app.dpapi'
if ($Capture) {
    if (Test-Path -LiteralPath $store) { throw 'Execution credentials already captured. Do not silently overwrite them.' }
    $listener = [Net.HttpListener]::new()
    $base = 'http://127.0.0.1:48357/'
    $listener.Prefixes.Add($base)
    $nonce = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $listener.Start()
    Write-Host "Secure local setup form: $base (no credentials are logged)"
    try {
        while ($listener.IsListening) {
            $context = $listener.GetContext()
            $context.Response.Headers.Add('Cache-Control', 'no-store')
            $context.Response.Headers.Add('Content-Security-Policy', "default-src 'none'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'")
            if ($context.Request.HttpMethod -eq 'GET' -and $context.Request.Url.AbsolutePath -eq '/') {
                $html = "<!doctype html><title>Sarthico secure execution setup</title><h1>Upstox execution credentials</h1><p>Stored encrypted for this Windows user. No token copying into chat.</p><form method='post' action='/save'><input type='hidden' name='nonce' value='$nonce'><p><label>App key <input name='key' type='password' autocomplete='off' required></label></p><p><label>App secret <input name='secret' type='password' autocomplete='off' required></label></p><button>Save encrypted</button></form>"
            } elseif ($context.Request.HttpMethod -eq 'POST' -and $context.Request.Url.AbsolutePath -eq '/save' -and
                $context.Request.Headers['Origin'] -eq $base.TrimEnd('/') -and $context.Request.ContentLength64 -in 1..32768) {
                $reader = [IO.StreamReader]::new($context.Request.InputStream)
                try { $body = $reader.ReadToEnd() } finally { $reader.Dispose() }
                $fields = @{}
                foreach ($pair in $body.Split('&')) {
                    $parts = $pair.Split('=', 2)
                    if ($parts.Count -eq 2) { $fields[[Net.WebUtility]::UrlDecode($parts[0])] = [Net.WebUtility]::UrlDecode($parts[1]) }
                }
                if ($fields.nonce -cne $nonce -or $fields.key.Length -lt 16 -or $fields.secret.Length -lt 8 -or
                    $fields.key.Length -gt 2048 -or $fields.secret.Length -gt 8192) { throw 'Invalid secure setup request.' }
                $folder = Split-Path $store
                New-Item -ItemType Directory -Force -Path $folder | Out-Null
                $plain = @{ ClientId = $fields.key; ClientSecret = $fields.secret } | ConvertTo-Json -Compress
                $secure = ConvertTo-SecureString $plain -AsPlainText -Force
                try { [IO.File]::WriteAllText($store, (ConvertFrom-SecureString $secure)) } finally { $secure.Dispose() }
                $plain = $null; $fields = $null; $body = $null
                $html = '<!doctype html><title>Saved</title><h1>Execution credentials saved encrypted.</h1><p>No live entries enabled. You may close this tab.</p>'
                $done = $true
            } else { $context.Response.StatusCode = 400; $html = 'Invalid setup request.' }
            $bytes = [Text.Encoding]::UTF8.GetBytes($html)
            $context.Response.ContentType = 'text/html; charset=utf-8'
            $context.Response.ContentLength64 = $bytes.Length
            $context.Response.OutputStream.Write($bytes)
            $context.Response.Close()
            if ($done) { break }
        }
    } finally { $listener.Stop(); $listener.Close(); $nonce = $null }
    return
}
if (-not $Activate -or -not $AccountId -or -not $VerifiedEgressIp) { throw 'Choose -Capture or supply approved -Activate, account and verified outbound IP.' }
if (-not $ExpectedInfrastructureHash) { throw 'Supply the checked Upstox release assembly hash before activation.' }
$indiaNow = [DateTimeOffset]::UtcNow.ToOffset([TimeSpan]::FromMinutes(330))
if ($indiaNow.DayOfWeek -notin @('Saturday', 'Sunday') -and $indiaNow.Hour -ge 8 -and $indiaNow.Hour -lt 16) {
    throw 'Website settings can restart capture. Activate only after capture has finished.'
}
if (-not (Test-Path -LiteralPath $store)) { throw 'Approved encrypted execution app credentials are missing.' }
if (-not $PSCmdlet.ShouldProcess('Sarthico website only', 'Configure Upstox execution credentials; no arming, recorder change or order')) { return }
$subscription = '8f1fb990-d44b-4dd2-b8f4-4208cc40c340'
$address = "https://management.azure.com/subscriptions/$subscription/resourceGroups/rg-sarthi-trading-centralindia/providers/Microsoft.Web/sites/sarthitradingweb5amvyarepxaci/config/appsettings"
try {
    $encrypted = ConvertTo-SecureString ([IO.File]::ReadAllText($store))
    $app = ([Net.NetworkCredential]::new('', $encrypted).Password | ConvertFrom-Json)
    $azureToken = az account get-access-token --subscription $subscription --resource https://management.azure.com/ --query accessToken -o tsv
    if ($LASTEXITCODE -ne 0 -or -not $azureToken) { throw 'Azure sign-in required.' }
    $headers = @{ Authorization = "Bearer $azureToken" }
    $package = Invoke-WebRequest -Uri 'https://sarthitradingweb5amvyarepxaci.scm.azurewebsites.net/api/vfs/site/wwwroot/TradingSystem.Infrastructure.dll' -Headers $headers -TimeoutSec 30
    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$package.Content)) -ne $ExpectedInfrastructureHash) {
        throw 'Website is not running the checked execution package.'
    }
    $current = Invoke-RestMethod -Method Post -Uri "$address/list?api-version=2024-11-01" -Headers $headers -TimeoutSec 30
    if ($current.properties.ApplicationRole -eq 'ReplayRecorder') { throw 'Wrong host role.' }
    $values = @{}; foreach ($p in $current.properties.PSObject.Properties) { $values[$p.Name] = $p.Value }
    $values['UpstoxExecution__ClientId'] = $app.ClientId
    $values['UpstoxExecution__ClientSecret'] = $app.ClientSecret
    $values['UpstoxExecution__AccountId'] = $AccountId
    $values['UpstoxExecution__Enabled'] = 'true'
    $values['UpstoxExecution__StaticIpVerified'] = 'true'
    $values['UpstoxExecution__VerifiedEgressIp'] = $VerifiedEgressIp
    $values['UpstoxExecution__ApiBaseUrl'] = 'https://relay.174-138-120-226.nip.io/upstox/'
    $values['UpstoxExecution__RedirectUri'] = 'https://sarthico.com/api/upstox-execution/callback'
    Invoke-RestMethod -Method Put -Uri "${address}?api-version=2024-11-01" -Headers $headers -ContentType 'application/json' -Body (@{ properties = $values } | ConvertTo-Json -Compress -Depth 4) | Out-Null
    $saved = Invoke-RestMethod -Method Post -Uri "$address/list?api-version=2024-11-01" -Headers $headers
    foreach ($key in $values.Keys | Where-Object { $_ -like 'UpstoxExecution__*' }) {
        if ($saved.properties.$key -cne $values[$key]) { throw 'Saved setup differs.' }
    }
    Write-Host 'Upstox execution app configured on website. Trading authorization and controlled order verification remain separate; markets were not armed.'
} catch { throw 'Upstox execution setup could not be confirmed. Credentials and response bodies withheld.' }
finally { if ($encrypted) { $encrypted.Dispose() }; $app=$null; $azureToken=$null; $headers=$null; $values=$null; $current=$null; $saved=$null }
