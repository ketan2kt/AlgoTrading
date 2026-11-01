[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('nifty', 'sensex', 'bank-nifty')]
    [string]$Market,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d{4}-\d{2}-\d{2}$')]
    [string]$From,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d{4}-\d{2}-\d{2}$')]
    [string]$To,
    [Parameter(Mandatory = $true)]
    [ValidateCount(1, 20)]
    [string[]]$RemoveStrategy,
    [ValidatePattern('^[a-z0-9][a-z0-9-]{4,63}$')]
    [string]$ReviewId
)

$ErrorActionPreference = 'Stop'
$start = [DateOnly]::ParseExact($From, 'yyyy-MM-dd')
$end = [DateOnly]::ParseExact($To, 'yyyy-MM-dd')
if ($end.DayNumber -lt $start.DayNumber -or $end.DayNumber - $start.DayNumber -gt 30) {
    throw 'Choose an ordered date range of at most 31 calendar days.'
}

$regular = [System.Collections.Generic.List[object]]::new()
$sessionCounts = [System.Collections.Generic.List[object]]::new()
for ($date = $start; $date.DayNumber -le $end.DayNumber; $date = $date.AddDays(1)) {
    if ($date.DayOfWeek -in @([DayOfWeek]::Saturday, [DayOfWeek]::Sunday)) { continue }
    $day = $date.ToString('yyyy-MM-dd')
    $queryMarket = if ($Market -eq 'bank-nifty') { 'all' } else { $Market }
    $json = & (Join-Path $PSScriptRoot 'tasks.ps1') analysis -Date $day -Market $queryMarket
    $report = $json | ConvertFrom-Json
    if ($report.tradingDate -ne $day) { throw "Unexpected report date for $day." }
    $trades = @($report.trades | Where-Object {
        $_.status -ne 'Active' -and
        $_.market -eq $Market.ToUpperInvariant() -and
        $_.strategy -notlike 'Hero Zero|*' -and
        $_.strategy -notlike 'Expansion Pair|*' -and
        $_.strategy -notlike 'PaperOnly|Pyramid|*'
    })
    $sessionCounts.Add([pscustomobject]@{ Date = $day; RegularEntries = $trades.Count })
    foreach ($trade in $trades) { $regular.Add($trade) }
}

$removed = @($regular | Where-Object {
    $strategy = $_.strategy
    @($RemoveStrategy | Where-Object { $strategy -like $_ }).Count -gt 0
})
$retained = $regular.Count - $removed.Count
$breakdown = @($regular | Group-Object strategy | Sort-Object Count -Descending | ForEach-Object {
    $strategy = $_.Name
    [pscustomobject]@{
        strategy = $strategy
        count = $_.Count
        netPnl = [math]::Round(($_.Group | Measure-Object netRealisedPnl -Sum).Sum, 2)
        removed = @($RemoveStrategy | Where-Object { $strategy -like $_ }).Count -gt 0
    }
})
Write-Output "Market: $Market; period: $From to $To; regular entries: $($regular.Count); proposed removal: $($removed.Count); historically retained: $retained."
foreach ($session in $sessionCounts) {
    Write-Output "  $($session.Date): $($session.RegularEntries) regular entries"
}
foreach ($group in $breakdown) {
    Write-Output "  $($group.count) x $($group.strategy): net $($group.netPnl); removed=$($group.removed)"
}
if ($ReviewId) {
    $reviewDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) 'docs/entry-path-reviews'
    $reviewPath = Join-Path $reviewDirectory "$ReviewId.json"
    if (Test-Path -LiteralPath $reviewPath) { throw "Review already exists: $reviewPath" }
    New-Item -ItemType Directory -Path $reviewDirectory -Force | Out-Null
    [pscustomobject]@{
        source = 'read-only diagnostics'
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        market = $Market
        from = $From
        to = $To
        removeStrategies = @($RemoveStrategy)
        historicalRegularEntries = $regular.Count
        historicallyRemoved = $removed.Count
        historicallyRetained = $retained
        sessions = @($sessionCounts)
        strategyBreakdown = @($breakdown | Select-Object strategy, count, removed)
        limitation = 'Historical path use is not proof of future signals, executable fills, or profitability.'
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reviewPath -Encoding utf8
    Write-Output "Review: $reviewPath"
}
if ($regular.Count -eq 0) {
    throw 'Impact check failed: no historical regular entries in this window. Do not infer the remaining path is viable.'
}
if ($retained -eq 0) {
    throw 'Impact check failed: the proposed removal leaves zero historically observed regular entries. Review the entry path before publishing.'
}
Write-Output 'Impact check passed: at least one regular entry path has historical usage. This does not prove future signal frequency or profitability.'
