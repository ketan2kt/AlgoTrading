$ErrorActionPreference = 'Stop'
# Synthetic package and HTTP doubles only; no Azure access or application code.
function az { $global:LASTEXITCODE = 0; return 'synthetic-test-token' }
function Start-Sleep { param($Seconds) }
function Invoke-WebRequest {
    param($Uri, $Headers, $TimeoutSec)
    $global:packageProbeRequests++
    if ($global:packageProbeTransient -and $global:packageProbeRequests -eq 1) { throw 'synthetic HTTP 502' }
    $name = ($Uri -split '/')[-1]
    if ($name -notin $global:packageProbeNames) { throw 'Unexpected probe URL.' }
    $bytes = if ($global:packageProbeMismatch) { [Text.Encoding]::UTF8.GetBytes('wrong-build') } else { [Text.Encoding]::UTF8.GetBytes($name) }
    return [pscustomobject]@{ Content = $bytes }
}
$testDirectory = [IO.Directory]::CreateTempSubdirectory('sarthi-package-test-').FullName
try {
    $global:packageProbeNames = @('TradingSystem.Api.dll', 'TradingSystem.Application.dll', 'TradingSystem.Infrastructure.dll', 'TradingSystem.Domain.dll')
    $path = Join-Path $testDirectory 'synthetic.zip'
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $global:packageProbeNames) {
            $stream = $zip.CreateEntry($name).Open()
            try { $stream.Write([Text.Encoding]::UTF8.GetBytes($name)) } finally { $stream.Dispose() }
        }
    } finally { $zip.Dispose() }
    $global:packageProbeRequests = 0
    $global:packageProbeTransient = $true
    $global:packageProbeMismatch = $false
    & "$PSScriptRoot/verify-recorder-package.ps1" -PackagePath $path -Attempts 2
    if ($global:packageProbeRequests -ne 5) { throw 'Transient upload/read failure did not verify all four assemblies.' }
    Write-Output 'PASS: transient-gateway-recovery-with-exact-package'

    $global:packageProbeRequests = 0
    $global:packageProbeTransient = $false
    $global:packageProbeMismatch = $true
    $rejected = $false
    try { & "$PSScriptRoot/verify-recorder-package.ps1" -PackagePath $path -Attempts 1 }
    catch {
        if ($_.Exception.Message -notlike 'Recorder package identity was not verified;*') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'A mismatched package was allowed to activate.' }
    Write-Output 'PASS: wrong-package-remains-blocked'
} finally {
    # Only our newly created disposable test directory, never a user path.
    [IO.Directory]::Delete($testDirectory, $true)
}
