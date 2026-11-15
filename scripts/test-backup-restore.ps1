[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PostgresBin,
    [ValidateRange(1024,65535)][int]$Port = 55483
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskRun = Join-Path $taskRoot ('.tools/recovery-drill-' + [Guid]::NewGuid().ToString('N'))
$taskData = Join-Path $taskRun 'cluster'
$taskSourceKeys = Join-Path $taskRun 'source-keys'
$taskBackupKeys = Join-Path $taskRun 'backup-keys'
$taskRestoredKeys = Join-Path $taskRun 'restored-keys'
$taskDump = Join-Path $taskRun 'synthetic.dump'
$taskStarted = $false
$taskWatch = [Diagnostics.Stopwatch]::StartNew()
function Invoke-DrillCommand([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Recovery command failed: $([IO.Path]::GetFileName($Executable)) ($LASTEXITCODE)" }
}
foreach ($taskBinary in @('initdb','pg_ctl','pg_dump','pg_restore','psql','createdb')) {
    if (-not (Test-Path (Join-Path $PostgresBin "$taskBinary.exe") -PathType Leaf)) { throw "Missing PostgreSQL binary: $taskBinary" }
}
# No production connection is accepted. This disposable cluster has synthetic data,
# loopback-only networking, and a dedicated OS-created directory. Trust is local only.
New-Item -ItemType Directory -Path $taskRun,$taskSourceKeys,$taskBackupKeys,$taskRestoredKeys | Out-Null
try {
    Invoke-DrillCommand (Join-Path $PostgresBin 'initdb.exe') @('-D',$taskData,'-U','recovery_drill','-A','trust','--encoding=UTF8','--no-locale')
    Invoke-DrillCommand (Join-Path $PostgresBin 'pg_ctl.exe') @('-D',$taskData,'-l',(Join-Path $taskRun 'postgres.log'),'-o',"-h 127.0.0.1 -p $Port",'-w','start')
    $taskStarted = $true
    foreach ($taskDb in @('recovery_drill_source','recovery_drill_restored')) {
        Invoke-DrillCommand (Join-Path $PostgresBin 'createdb.exe') @('-h','127.0.0.1','-p',"$Port",'-U','recovery_drill',$taskDb)
    }
    $taskProject = Join-Path $taskRoot 'tools/RecoveryDrill/RecoveryDrill.csproj'
    Invoke-DrillCommand 'dotnet' @('build',$taskProject,'-c','Release','--nologo','-v','quiet')
    $taskDll = Join-Path $taskRoot 'tools/RecoveryDrill/bin/Release/net10.0/RecoveryDrill.dll'
    Invoke-DrillCommand 'dotnet' @($taskDll,'seed',"$Port",'recovery_drill_source',$taskSourceKeys)
    Invoke-DrillCommand (Join-Path $PostgresBin 'pg_dump.exe') @('-h','127.0.0.1','-p',"$Port",'-U','recovery_drill','-Fc','--file',$taskDump,'recovery_drill_source')
    Copy-Item -Path (Join-Path $taskSourceKeys '*.xml') -Destination $taskBackupKeys
    $taskDumpHash = (Get-FileHash -LiteralPath $taskDump -Algorithm SHA256).Hash
    # Stop and restart PostgreSQL before restoring; source DB and source keys remain untouched.
    Invoke-DrillCommand (Join-Path $PostgresBin 'pg_ctl.exe') @('-D',$taskData,'-m','fast','-w','stop')
    $taskStarted = $false
    Invoke-DrillCommand (Join-Path $PostgresBin 'pg_ctl.exe') @('-D',$taskData,'-l',(Join-Path $taskRun 'postgres.log'),'-o',"-h 127.0.0.1 -p $Port",'-w','start')
    $taskStarted = $true
    if ((Get-FileHash -LiteralPath $taskDump -Algorithm SHA256).Hash -ne $taskDumpHash) { throw 'Backup checksum changed.' }
    Invoke-DrillCommand (Join-Path $PostgresBin 'pg_restore.exe') @('-h','127.0.0.1','-p',"$Port",'-U','recovery_drill','--exit-on-error','--single-transaction','-d','recovery_drill_restored',$taskDump)
    Invoke-DrillCommand 'dotnet' @($taskDll,'verify-missing-keys',"$Port",'recovery_drill_restored',(Join-Path $taskRun 'missing-keys'))
    Copy-Item -Path (Join-Path $taskBackupKeys '*.xml') -Destination $taskRestoredKeys
    foreach ($taskKey in Get-ChildItem -LiteralPath $taskBackupKeys -Filter '*.xml') {
        if ((Get-FileHash -LiteralPath $taskKey.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $taskRestoredKeys $taskKey.Name)).Hash) { throw 'Key-ring checksum mismatch.' }
    }
    Invoke-DrillCommand 'dotnet' @($taskDll,'verify',"$Port",'recovery_drill_restored',$taskRestoredKeys)
    Write-Host "PASS: isolated synthetic dump/restore; $([Math]::Round($taskWatch.Elapsed.TotalSeconds,1)) seconds. Evidence retained at $taskRun. No production data or Azure restore used."
} finally {
    if ($taskStarted) { Invoke-DrillCommand (Join-Path $PostgresBin 'pg_ctl.exe') @('-D',$taskData,'-m','fast','-w','stop') }
}
