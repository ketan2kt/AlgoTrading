[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Base,
    [Parameter(Mandatory = $true)]
    [string]$Head,
    [string]$ReviewOnly
)

$ErrorActionPreference = 'Stop'
if ($ReviewOnly) {
    $reviews = @($ReviewOnly)
} else {
    $changed = @(& git diff --name-only $Base $Head)
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the proposed changes.' }
    $entryCode = @($changed | Where-Object {
        $_ -match '^src/TradingSystem\.Application/(Strategies|Execution)/' -or
        $_ -match '^src/TradingSystem\.Infrastructure/Execution/(AutomatedPaperTradingService|MultiMarketPaperTradingService|HeroZeroPaperTradingService|IntradayExpansionPairService)\.cs$'
    })
    if ($entryCode.Count -eq 0) {
        Write-Output 'Entry-path review: no entry-decision modules changed.'
        return
    }
    $reviews = @($changed | Where-Object { $_ -match '^docs/entry-path-reviews/[a-z0-9-]+\.json$' })
    if ($reviews.Count -eq 0) {
        throw "Entry-decision code changed ($($entryCode -join ', ')), but no new docs/entry-path-reviews/*.json was committed. Run check-entry-path-impact.ps1 with -ReviewId and include the generated review."
    }
}

foreach ($path in $reviews) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Review file missing: $path" }
    $review = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($review.source -ne 'read-only diagnostics' -or
        $review.market -notin @('nifty', 'sensex', 'bank-nifty') -or
        @($review.removeStrategies).Count -eq 0 -or
        @($review.strategyBreakdown).Count -eq 0 -or
        [string]::IsNullOrWhiteSpace($review.limitation)) {
        throw "Incomplete entry-path evidence in $path."
    }
    $from = [DateOnly]::ParseExact($review.from, 'yyyy-MM-dd')
    $to = [DateOnly]::ParseExact($review.to, 'yyyy-MM-dd')
    if ($to.DayNumber -lt $from.DayNumber -or $to.DayNumber - $from.DayNumber -gt 30) {
        throw "Invalid review window in $path."
    }
    $total = [int]$review.historicalRegularEntries
    $removed = [int]$review.historicallyRemoved
    $retained = [int]$review.historicallyRetained
    $breakdownCount = 0
    $breakdownRemoved = 0
    foreach ($group in @($review.strategyBreakdown)) {
        if ([int]$group.count -le 0 -or [string]::IsNullOrWhiteSpace($group.strategy)) {
            throw "Invalid strategy breakdown in $path."
        }
        $matchesRemoval = @($review.removeStrategies | Where-Object {
            $group.strategy -like $_
        }).Count -gt 0
        if ($matchesRemoval -ne [bool]$group.removed) {
            throw "Removal classification does not match the patterns in $path."
        }
        $breakdownCount += [int]$group.count
        if ($matchesRemoval) { $breakdownRemoved += [int]$group.count }
    }
    if ($total -le 0 -or $removed -lt 0 -or $retained -le 0 -or
        $total -ne $removed + $retained -or $total -ne $breakdownCount -or
        $removed -ne $breakdownRemoved) {
        throw "Entry-path review failed in ${path}: zero or inconsistent historically retained regular entries."
    }
    Write-Output "Entry-path review passed: $path ($($review.market): $retained/$total regular entries historically retained)."
}
