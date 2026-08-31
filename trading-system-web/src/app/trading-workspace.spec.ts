import { describe, expect, it } from 'vitest';
import { mergeWorkspaceSnapshot, TradingWorkspaceSnapshot, WorkspaceCandle } from './trading-workspace';

describe('mergeWorkspaceSnapshot', () => {
  it('does not let a stale realtime snapshot move the chart back to an older session', () => {
    const current = snapshot([
      candle('2026-08-28T03:45:00Z'),
      candle('2026-08-28T03:50:00Z'),
    ]);
    const stale = snapshot([candle('2026-08-26T06:20:00Z')]);
    stale.feedStatus = 'Connected';

    const result = mergeWorkspaceSnapshot(current, stale);

    expect(result.candles).toEqual(current.candles);
    expect(result.feedStatus).toBe('Connected');
  });

  it('accepts a snapshot that advances to a newer session', () => {
    const current = snapshot([candle('2026-08-28T09:55:00Z')]);
    const monday = snapshot([candle('2026-08-31T03:45:00Z')]);

    expect(mergeWorkspaceSnapshot(current, monday)).toBe(monday);
  });

  it('keeps the more complete snapshot when both end at the same candle', () => {
    const complete = snapshot([
      candle('2026-08-28T03:45:00Z'),
      candle('2026-08-28T03:50:00Z'),
    ]);
    const partial = snapshot([candle('2026-08-28T03:50:00Z')]);

    expect(mergeWorkspaceSnapshot(complete, partial).candles).toEqual(complete.candles);
  });
});

function candle(openTimeUtc: string): WorkspaceCandle {
  return {
    openTimeUtc,
    intervalSeconds: 60,
    open: 100,
    high: 101,
    low: 99,
    close: 100,
    volume: 1,
    isClosed: true,
  };
}

function snapshot(candles: WorkspaceCandle[]): TradingWorkspaceSnapshot {
  return {
    instrument: 'NIFTY',
    exchange: 'NSE',
    timeframe: '1m',
    mode: 'Paper',
    feedStatus: 'Stale',
    isLive: false,
    isFresh: false,
    lastMarketTimestampUtc: null,
    observedAtUtc: '2026-08-31T03:45:00Z',
    statusMessage: null,
    candles,
    overlays: [],
    evaluations: [],
    paperAutomation: {
      status: 'Ready',
      tradingPermitted: true,
      message: '',
      observedAtUtc: '2026-08-31T03:45:00Z',
      tradesToday: 0,
      realisedPnl: 0,
      unrealisedPnl: 0,
      activeSignalId: null,
      activeDirection: null,
      activeQuantity: null,
      entryPrice: null,
      stopLoss: null,
      target: null,
      selectedOptionSymbol: null,
      selectedOptionType: null,
      selectedOptionExpiry: null,
      selectedOptionStrike: null,
      selectedOptionLotSize: null,
      readinessChecks: [],
      currentOptionPrice: null,
    },
    futuresVolume: [],
  };
}
