import { describe, expect, it } from 'vitest';
import { CaptureIndicatorHealth, captureAlertText, captureIndicator } from './capture-alert';

describe('compact replay alerts', () => {
  const legacy = 'Feed Interrupted · Recording: Transport interrupted or invalid frame';

  it('does not turn the ambiguous legacy warning into a confirmed gap', () => {
    expect(captureAlertText([legacy])).toBe('Feed warning');
    expect(captureAlertText([legacy, 'Quote recording gap detected'])).toBe('Quote gap detected');
  });

  it('names the affected markets using the same short codes as trade alerts', () => {
    expect(captureAlertText(['NIFTY option price/depth recording missing or stale',
      'SENSEX option price/depth recording missing or stale', 'BANKNIFTY option price/depth recording missing or stale']))
      .toBe('N/S/BN option data stale or missing');
  });
});

describe('replay indicator severity', () => {
  const checkedAt = '2026-11-19T04:00:00Z';
  const now = Date.parse(checkedAt);
  const healthy: CaptureIndicatorHealth = {checkedAt, expectedNow: true, issues: [], feed: {phase: 'Subscribed'}};
  const classify = (changes: Partial<CaptureIndicatorHealth> = {}) => captureIndicator({...healthy, ...changes}, false, now);
  const backup = {
    lastUpstoxOptionWrittenAtByMarket: {NIFTY: checkedAt, SENSEX: checkedAt, BANKNIFTY: checkedAt},
    upstoxCoverage: {NIFTY: {planned: 10, mapped: 10}, SENSEX: {planned: 10, mapped: 10}, BANKNIFTY: {planned: 10, mapped: 10}}
  };
  it('uses green only for a fresh active check with no issues', () => {
    expect(classify()).toEqual({severity: 'healthy', text: 'Recording OK'});
    expect(classify({feed: {phase: 'Connecting'}}).severity).toBe('warning');
    expect(captureIndicator(null, false, now).severity).toBe('warning');
    expect(captureIndicator(healthy, true, now).severity).toBe('warning');
    expect(captureIndicator(healthy, false, now + 60001).severity).toBe('warning');
    expect(classify({checkedAt: 'invalid'}).severity).toBe('warning');
    expect(captureIndicator(healthy, false, now - 1).severity).toBe('warning');
  });
  it('keeps historical gaps and preflight warnings amber, not critical', () => {
    for (const issue of ['Feed interruption recorded', 'Quote recording gap detected',
      'Feed Interrupted · Recording: Transport interrupted or invalid frame', 'Pre-open strategy/settings receipt missing'])
      expect(classify({issues: [issue]}).severity).toBe('warning');
  });
  it('makes current missing option recording critical for each market unless its mapped backup is fresh', () => {
    for (const market of ['NIFTY', 'SENSEX', 'BANKNIFTY']) {
      const issues = [`${market} option price/depth recording missing or stale`];
      expect(classify({issues}).severity).toBe('critical');
      expect(classify({issues, backupStorage: backup}).severity).toBe('warning');
      for (const time of ['2026-11-19T03:59:29Z', '2026-11-19T04:00:01Z', 'invalid'])
        expect(classify({issues, backupStorage: {...backup,
          lastUpstoxOptionWrittenAtByMarket: {...backup.lastUpstoxOptionWrittenAtByMarket, [market]: time}}}).severity).toBe('critical');
      expect(classify({issues, backupStorage: {...backup, upstoxFailures: {[market]: 'Unauthorized'}}}).severity).toBe('critical');
      expect(classify({issues, backupStorage: {...backup,
        upstoxCoverage: {...backup.upstoxCoverage, [market]: {planned: 10, mapped: 9}}}}).severity).toBe('critical');
    }
  });
  it('treats storage/no-write failures as critical unless all three have working backup recording', () => {
    for (const issue of ['No quote write confirmed in the last 30 seconds', 'Private archive upload failed · IOException']) {
      expect(classify({issues: [issue]}).severity).toBe('critical');
      expect(classify({issues: [issue], backupStorage: backup}).severity).toBe('warning');
    }
    expect(classify({storage: {failure: 'Quote archive storage limit / insufficient disk space'}}).severity).toBe('critical');
    expect(classify({storage: {failure: 'Disk full'}, backupStorage: backup}).severity).toBe('warning');
    expect(classify({issues: ['SENSEX option price/depth recording missing or stale'],
      backupStorage: {...backup, failure: 'Disk full'}}).severity).toBe('critical');
  });
  it('shows neutral outside capture hours instead of green or a historical red alert', () => {
    expect(classify({expectedNow: false, issues: ['No quote write confirmed in the last 30 seconds']}))
      .toEqual({severity: 'idle', text: 'Outside capture hours'});
  });
});
