[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('status', 'check', 'deploy', 'runs', 'failed-logs', 'analysis', 'ops', 'replay', 'context')]
    [string]$Task = 'status',
    [ValidateSet('backend', 'frontend', 'all')]
    [string]$Scope = 'all',
    [string]$Filter,
    [string]$Ref,
    [ValidatePattern('^\d+$')]
    [string]$RunId,
    [ValidateSet('nifty', 'sensex', 'all')]
    [string]$Market = 'all',
    [ValidatePattern('^\d{4}-\d{2}-\d{2}$')]
    [string]$Date,
    [ValidateRange(1, 90)]
    [int]$Days = 30,
    [switch]$Install,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$repo = 'ketan2kt/AlgoTrading'
$workflow = 'azure-deploy.yml'
$root = Split-Path $PSScriptRoot -Parent
$originalPath = $env:PATH

function Invoke-Checked {
    param([string]$Command, [string[]]$Arguments)
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed (exit $LASTEXITCODE)." }
}

function Invoke-Captured {
    param([string]$Command, [string[]]$Arguments)
    $captured = & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed (exit $LASTEXITCODE)." }
    return ($captured -join "`n").Trim()
}

function Assert-DeploymentCiPassed {
    param([string]$RemoteRef)

    $encodedRef = [Uri]::EscapeDataString($RemoteRef)
    $sha = Invoke-Captured gh @('api', "repos/$repo/commits/$encodedRef", '--jq', '.sha')
    if ($sha -notmatch '^[0-9a-f]{40}$') { throw "Could not resolve remote ref '$RemoteRef' to a commit." }
    $runsJson = Invoke-Captured gh @('run', 'list', '--repo', $repo,
        '--workflow', 'ci.yml', '--commit', $sha, '--event', 'push',
        '--limit', '10', '--json', 'databaseId,headSha,status,conclusion')
    $run = @(ConvertFrom-Json -InputObject $runsJson) |
        Where-Object { $_.headSha -eq $sha } | Select-Object -First 1
    if (-not $run) { throw "No push CI run exists for $sha. Deployment not dispatched." }
    if ($run.status -ne 'completed' -or $run.conclusion -ne 'success') {
        throw "CI run $($run.databaseId) for $sha is $($run.status)/$($run.conclusion). Deployment not dispatched."
    }
    Write-Output "CI passed for $sha (run $($run.databaseId))."
}

function Get-DiagnosticsContext {
    $resourceGroup = Invoke-Captured gh @(
        'variable', 'get', 'AZURE_RESOURCE_GROUP', '--repo', $repo)
    $app = Invoke-Captured az @(
        'webapp', 'list', '--resource-group', $resourceGroup,
        '--query', "[?contains(name, 'sarthitradingweb')].name | [0]", '-o', 'tsv')
    if ([string]::IsNullOrWhiteSpace($app)) {
        throw "No Sarthi trading web app was found in '$resourceGroup'."
    }
    $diagnosticsKey = Invoke-Captured az @(
        'webapp', 'config', 'appsettings', 'list',
        '--resource-group', $resourceGroup, '--name', $app,
        '--query', "[?name=='Diagnostics__ReadOnlyApiKey'].value | [0]", '-o', 'tsv')
    if ([string]::IsNullOrWhiteSpace($diagnosticsKey)) {
        throw 'The read-only diagnostics API is not configured. Deploy the diagnostics-capable build first.'
    }
    return [pscustomobject]@{
        App = $app
        Key = $diagnosticsKey
    }
}

function Invoke-DiagnosticsGet {
    param([string]$Path)
    $context = Get-DiagnosticsContext
    try {
        return Invoke-RestMethod -Method Get `
            -Uri "https://$($context.App).azurewebsites.net$Path" `
            -Headers @{ 'X-Sarthi-Diagnostics-Key' = $context.Key }
    } finally {
        $context.Key = $null
        $context = $null
    }
}

Push-Location $root
try {
    # Prefer the repository's pinned Node runtime when it is installed.
    $nodeDirectory = Join-Path $root '.tools/node-v24.15.0-win-x64'
    if (Test-Path (Join-Path $nodeDirectory 'node.exe')) {
        $env:PATH = "$nodeDirectory;$env:PATH"
    }
    switch ($Task) {
        'status' {
            Invoke-Checked git @('status', '--short', '--branch')
            Invoke-Checked git @('log', '-3', '--oneline')
            Write-Output "Deployment progress: https://github.com/$repo/actions/workflows/$workflow"
        }
        'check' {
            if ($Scope -in @('backend', 'all')) {
                $testArguments = @('test', 'TradingSystem.slnx', '--configuration', 'Release', '--nologo')
                if ($Filter) { $testArguments += @('--filter', $Filter) }
                Invoke-Checked dotnet $testArguments
            }
            if ($Scope -in @('frontend', 'all')) {
                Push-Location (Join-Path $root 'trading-system-web')
                try {
                    if ($Install) { Invoke-Checked npm.cmd @('ci') }
                    Invoke-Checked npm.cmd @('run', 'build')
                    Invoke-Checked npm.cmd @('run', 'test:ci')
                } finally { Pop-Location }
            }
            Write-Output "Checks passed: $Scope. PostgreSQL coverage requires RUN_POSTGRES_TESTS=true and Docker."
        }
        'deploy' {
            if ([string]::IsNullOrWhiteSpace($Ref)) { throw 'Supply -Ref with the authorized remote branch or tag.' }
            Assert-DeploymentCiPassed $Ref
            $dispatchArguments = @('workflow', 'run', $workflow, '--repo', $repo, '--ref', $Ref, '-f', 'deploy=DEPLOY-LIVE')
            if ($DryRun) {
                Write-Output ('Would invoke gh with arguments: ' + ($dispatchArguments | ConvertTo-Json -Compress))
                Write-Output 'No deployment dispatched.'
            } else {
                Invoke-Checked gh $dispatchArguments
                Write-Output "Dispatch accepted for ref '$Ref'; completion has not been verified. Local changes are not uploaded."
            }
            Write-Output "Progress: https://github.com/$repo/actions/workflows/$workflow"
        }
        'runs' {
            Invoke-Checked gh @('run', 'list', '--repo', $repo, '--workflow', $workflow, '--limit', '5', '--json', 'databaseId,headSha,status,conclusion,url,createdAt')
        }
        'failed-logs' {
            if (-not $RunId) { throw 'Supply -RunId from the workflow URL or runs command.' }
            Invoke-Checked gh @('run', 'view', $RunId, '--repo', $repo, '--log-failed')
        }
        'analysis' {
            if ([string]::IsNullOrWhiteSpace($Date)) {
                $Date = Get-Date -Format 'yyyy-MM-dd'
            }
            Invoke-DiagnosticsGet "/api/internal/diagnostics/daily?date=$Date&market=$Market" |
                ConvertTo-Json -Depth 12
        }
        'ops' {
            $query = "market=$Market"
            if (-not [string]::IsNullOrWhiteSpace($Date)) { $query += "&date=$Date" }
            Invoke-DiagnosticsGet "/api/internal/diagnostics/operational?$query" |
                ConvertTo-Json -Depth 12
        }
        'replay' {
            Invoke-DiagnosticsGet "/api/internal/diagnostics/replay?days=$Days" |
                ConvertTo-Json -Depth 12
        }
        'context' {
            $manifest = Get-Content (Join-Path $root 'docs/project-state.json') -Raw |
                ConvertFrom-Json
            $manifest | Add-Member -NotePropertyName branch -NotePropertyValue (
                Invoke-Captured git @('branch', '--show-current')) -Force
            $manifest | Add-Member -NotePropertyName headCommit -NotePropertyValue (
                Invoke-Captured git @('rev-parse', 'HEAD')) -Force
            $manifest | Add-Member -NotePropertyName workingTree -NotePropertyValue @(
                & git status --short) -Force
            $manifest | ConvertTo-Json -Depth 10
        }
    }
} finally {
    $env:PATH = $originalPath
    Pop-Location
}
