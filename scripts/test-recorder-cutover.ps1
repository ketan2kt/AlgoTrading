$ErrorActionPreference = 'Stop'
# In-memory cloud/HTTP doubles: this test must never contact Azure.
function az { $global:LASTEXITCODE = 0; return 'synthetic-test-token' }
function Start-Sleep { param($Seconds) }
function Invoke-RestMethod {
    param($Method, $Uri, $Headers, $ContentType, $Body)
    $isRecorder = $Uri -like '*/sarthirecorder5amvyarepxaci/*'
    $key = if ($isRecorder) { 'recorder' } else { 'website' }
    if ($Method -eq 'Post' -and $Uri -like '*/appsettings/list?*') {
        return [pscustomobject]@{ properties = [pscustomobject]$global:recorderTestSettings[$key] }
    }
    if ($Method -eq 'Put' -and $Uri -like '*/appsettings?*') {
        $global:recorderTestSettings[$key] = ($Body | ConvertFrom-Json -AsHashtable).properties
        $global:recorderTestWrites.Add($key + ':' + $global:recorderTestSettings[$key]['MarketData__HistoricalReplayFeed__Enabled'])
        if ($global:recorderTestSettings.recorder['MarketData__HistoricalReplayFeed__Enabled'] -eq 'true' -and
            $global:recorderTestSettings.website['MarketData__HistoricalReplayFeed__Enabled'] -eq 'true') {
            throw 'Competing capture producers were enabled.'
        }
        return
    }
    throw 'Unexpected request in cutover test.'
}
function Invoke-WebRequest {
    param($Uri, $TimeoutSec)
    if ($Uri -ne 'https://sarthirecorder5amvyarepxaci.azurewebsites.net/health/recorder-active') {
        throw 'Unexpected health URL.'
    }
    $global:recorderTestProbes++
    if ($global:recorderTestSucceed -and $global:recorderTestProbes -eq 117) { return [pscustomobject]@{ StatusCode = 200 } }
    throw 'Replacement host is still starting.'
}

foreach ($mode in @('slow-success', 'failed-start')) {
    $global:recorderTestSettings = @{
        website = @{ ConnectionStrings__TradingDatabase = 'synthetic-database'; MarketData__HistoricalReplayFeed__Enabled = 'true' }
        recorder = @{ MarketData__HistoricalReplayFeed__Enabled = 'false' }
    }
    $global:recorderTestWrites = [Collections.Generic.List[string]]::new()
    $global:recorderTestProbes = 0
    $global:recorderTestSucceed = $mode -eq 'slow-success'
    $failed = $false
    try { & "$PSScriptRoot/configure-replay-recorder.ps1" -Activate -AllowMarketHoursRestart }
    catch {
        if ($_.Exception.Message -notlike 'Recorder cutover failed;*') { throw }
        $failed = $true
    }
    if ($global:recorderTestSettings.recorder.WEBSITES_CONTAINER_START_TIME_LIMIT -ne '600') { throw 'Startup budget missing.' }
    if ($global:recorderTestSucceed) {
        if ($failed -or $global:recorderTestProbes -ne 117 -or ($global:recorderTestWrites -join ',') -ne 'website:false,recorder:true') {
            throw 'Slow successful activation was not preserved.'
        }
    } elseif (-not $failed -or $global:recorderTestProbes -ne 120 -or
        ($global:recorderTestWrites -join ',') -ne 'website:false,recorder:true,recorder:false,website:true') {
        throw 'Failed activation did not restore capture in safe order.'
    }
    Write-Output "PASS: $mode"
}
