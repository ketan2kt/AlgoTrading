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

export interface CaptureIndicatorHealth {
  checkedAt: string;
  expectedNow: boolean;
  issues: readonly string[];
  feed: {phase: string};
  storage?: {failure?: string | null} | null;
  backupStorage?: {
    failure?: string | null;
    lastUpstoxOptionWrittenAtByMarket?: Record<string, string>;
    upstoxFailures?: Record<string, string>;
    upstoxCoverage?: Record<string, {planned: number; mapped: number}>;
  } | null;
}

export function captureIndicator(health: CaptureIndicatorHealth | null, unavailable: boolean, now: number):
  {severity: 'healthy' | 'warning' | 'critical' | 'idle'; text: string} {
  if (unavailable) return {severity: 'warning', text: 'Status unavailable'};
  if (!health) return {severity: 'warning', text: 'Checking…'};
  const checked = Date.parse(health.checkedAt);
  if (!Number.isFinite(checked) || checked > now || now - checked > 60000)
    return {severity: 'warning', text: 'Status stale'};
  if (!health.expectedNow) return {severity: 'idle', text: 'Outside capture hours'};

  const markets = ['NIFTY', 'SENSEX', 'BANKNIFTY'];
  const backupFresh = (market: string): boolean => {
    const backup = health.backupStorage;
    const at = Date.parse(backup?.lastUpstoxOptionWrittenAtByMarket?.[market] ?? '');
    const coverage = backup?.upstoxCoverage?.[market];
    return !backup?.failure && !backup?.upstoxFailures?.[market] &&
      !!coverage && coverage.planned > 0 && coverage.mapped === coverage.planned &&
      Number.isFinite(at) && at <= checked && checked - at <= 30000;
  };
  // A primary-feed interruption alone does not mean both recording sources failed.
  // Historical gaps stay amber; current missing writes without backup are critical.
  const missingMarket = markets.some(market =>
    health.issues.includes(`${market} option price/depth recording missing or stale`) && !backupFresh(market));
  const primaryFailure = !!health.storage?.failure || health.issues.some(issue =>
    issue === 'No quote write confirmed in the last 30 seconds' ||
    issue.startsWith('Private archive upload failed'));
  if (missingMarket || primaryFailure && !markets.every(backupFresh))
    return {severity: 'critical', text: 'Recording failed'};
  if (health.issues.length || primaryFailure || health.feed.phase !== 'Subscribed' || health.backupStorage?.failure)
    return {severity: 'warning', text: captureAlertText(health.issues)};
  return {severity: 'healthy', text: 'Recording OK'};
}
