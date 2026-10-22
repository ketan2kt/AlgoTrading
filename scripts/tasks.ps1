[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('status', 'check', 'deploy', 'runs', 'failed-logs', 'analysis')]
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
            $resourceGroup = Invoke-Captured gh @(
                'variable', 'get', 'AZURE_RESOURCE_GROUP', '--repo', $repo)
            $app = Invoke-Captured az @(
                'webapp', 'list', '--resource-group', $resourceGroup,
                '--query', "[?contains(name, 'sarthitradingweb')].name | [0]", '-o', 'tsv')
            if ([string]::IsNullOrWhiteSpace($app)) {
                throw "No Sarthi trading web app was found in '$resourceGroup'."
            }
            # Capture the dedicated read-only key without ever writing it to output.
            $diagnosticsKey = Invoke-Captured az @(
                'webapp', 'config', 'appsettings', 'list',
                '--resource-group', $resourceGroup, '--name', $app,
                '--query', "[?name=='Diagnostics__ReadOnlyApiKey'].value | [0]", '-o', 'tsv')
            if ([string]::IsNullOrWhiteSpace($diagnosticsKey)) {
                throw 'The read-only diagnostics API is not configured. Deploy the diagnostics-capable build first.'
            }
            try {
                $uri = "https://$app.azurewebsites.net/api/internal/diagnostics/daily?date=$Date&market=$Market"
                $report = Invoke-RestMethod -Method Get -Uri $uri -Headers @{
                    'X-Sarthi-Diagnostics-Key' = $diagnosticsKey
                }
                $report | ConvertTo-Json -Depth 12
            } finally {
                $diagnosticsKey = $null
            }
        }
    }
} finally {
    $env:PATH = $originalPath
    Pop-Location
}
