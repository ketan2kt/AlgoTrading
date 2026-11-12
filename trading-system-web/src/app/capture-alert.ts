export function captureAlertText(issues: readonly string[]): string {
  if (issues.includes('Quote recording gap detected')) return 'Quote gap detected';
  const markets = issues.flatMap(issue => {
    const code = /^(NIFTY|SENSEX|BANKNIFTY) option price\/depth recording missing or stale$/.exec(issue)?.[1];
    return code ? [code === 'NIFTY' ? 'N' : code === 'SENSEX' ? 'S' : 'BN'] : [];
  });
  if (markets.length) return `${[...new Set(markets)].join('/')} option data stale or missing`;
  if (issues.includes('Feed interruption recorded')) return 'Feed interruption recorded';
  if (issues.includes('Feed silent over 30 seconds')) return 'Feed silent >30s';
  if (issues.some(issue => issue.startsWith('Private archive upload failed'))) return 'Archive upload failed';
  if (issues.some(issue => issue.startsWith('Independent recorder heartbeat missing/stale'))) return 'Recorder heartbeat stale';
  if (issues.some(issue => issue.startsWith('Feed '))) return 'Feed warning';
  return 'Check recording';
}
