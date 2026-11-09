[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$PackagePath,
    [ValidateRange(1, 36)][int]$Attempts = 18)
$ErrorActionPreference = 'Stop'
$names = @('TradingSystem.Api.dll', 'TradingSystem.Application.dll', 'TradingSystem.Infrastructure.dll', 'TradingSystem.Domain.dll')
$expected = @{}
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $PackagePath))
try {
    foreach ($name in $names) {
        $entry = $zip.GetEntry($name)
        if ($null -eq $entry) { throw "Checked package missing $name." }
        $stream = $entry.Open()
        try { $expected[$name] = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
    }
} finally { $zip.Dispose() }
$access = az account get-access-token --query accessToken -o tsv
if ($LASTEXITCODE -ne 0) { throw 'Azure authentication failed.' }
$headers = @{ Authorization = "Bearer $access" }
try {
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        $matched = $true
        foreach ($name in $names) {
            try {
                $response = Invoke-WebRequest -Uri "https://sarthirecorder5amvyarepxaci.scm.azurewebsites.net/api/vfs/site/wwwroot/$name" `
                    -Headers $headers -TimeoutSec 20
                $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$response.Content))
                if ($hash -ne $expected[$name]) { $matched = $false; break }
            } catch { $matched = $false; break }
        }
        if ($matched) {
            Write-Output 'All four recorder assemblies match the exact checked release package.'
            return
        }
        if ($attempt -lt $Attempts) { Start-Sleep -Seconds 10 }
    }
    throw 'Recorder package identity was not verified; activation must not proceed.'
} finally { $access = $null; $headers = $null }
