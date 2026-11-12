import { describe, expect, it } from 'vitest';
import { buildMarketNarratorEvents, selectHighSignalNarratorEvents } from './market-narrator';
import { TradingWorkspaceSnapshot, WorkspaceCandle, WorkspaceTradeOverlay } from './trading-workspace';

function candle(time: string, open: number, high: number, low: number, close: number, volume = 100): WorkspaceCandle {
  return { openTimeUtc: time, intervalSeconds: 300, open, high, low, close, volume, isClosed: true };
}

function snapshot(candles: WorkspaceCandle[], overrides: Partial<TradingWorkspaceSnapshot> = {}): TradingWorkspaceSnapshot {
  const last = candles.at(-1)!;
  return {
    instrument: 'NIFTY', exchange: 'NSE', timeframe: '5m', mode: 'Paper', feedStatus: 'Live',
    isLive: true, isFresh: true, lastMarketTimestampUtc: last.openTimeUtc, observedAtUtc: last.openTimeUtc,
    statusMessage: null, candles, overlays: [], futuresVolume: null, research: null,
    evaluations: candles.map((item, index) => ({
      evaluationId: `${index}`, candleTimeUtc: item.openTimeUtc, strategy: 'test', outcome: 'Observed',
      currentPrice: item.close, openingRangeHigh: 102, openingRangeLow: 98, vwap: 100,
      fastEma: item.close, slowEma: 100, atrPercent: 0.2,
      relativeFuturesVolume: index === candles.length - 1 ? 1.8 : 1, regime: 'Trending', regimeBias: 'Bullish',
      regimeConfidence: 0.8, failedConditions: [], signalId: null, optionSymbol: null, optionType: null,
      optionExpiry: null, optionStrike: null, optionPremium: null, realisedPnl: null, shadowStructureState: null,
      shadowTrendQuality: null, shadowWouldPermit: null, shadowEvidence: [],
    })),
    paperAutomation: {
      status: 'Ready', tradingPermitted: true, message: 'Ready', observedAtUtc: last.openTimeUtc,
      tradesToday: 0, realisedPnl: 0, unrealisedPnl: 0, activeSignalId: null, activeDirection: null,
      activeQuantity: null, entryPrice: null, stopLoss: null, target: null, selectedOptionSymbol: null,
      selectedOptionType: null, selectedOptionExpiry: null, selectedOptionStrike: null, selectedOptionLotSize: null,
      readinessChecks: [], currentOptionPrice: null,
    }, ...overrides,
  };
}

describe('market narrator', () => {
  it.each([
    ['positive giveback', { unrealisedPnl: 800 }, true, 'Still in profit.'],
    ['breakeven', { unrealisedPnl: 0 }, true, 'at breakeven'],
    ['negative open mark', { unrealisedPnl: -200 }, true, 'now negative'],
    ['closed winner', { unrealisedPnl: 0, lifecycleStatus: 'Closed', realisedPnl: 1800 }, false, ''],
    ['partial exit', { unrealisedPnl: 800, quantity: 5 }, false, ''],
    ['different trade', { unrealisedPnl: 0, signalId: 'new-trade' }, false, ''],
    ['missing mark', { unrealisedPnl: null }, false, ''],
    ['missing price', { unrealisedPnl: 0, currentOptionPrice: null }, false, ''],
  ] as [string, Partial<WorkspaceTradeOverlay>, boolean, string][])(
    'uses per-trade evidence, not a zero automation summary: %s', (_name, changes, expected, text) => {
      const bars = [candle('2026-11-05T04:10:00Z', 100, 101, 99, 100)];
      const trade: WorkspaceTradeOverlay = {
        signalId: 's1', strategy: 'test', direction: 'Buy', signalTimeUtc: bars[0].openTimeUtc,
        entry: 100, stopLoss: 95, target: 110, status: 'Active', quantity: 10, fillPrice: 100,
        executionInstrument: 'NIFTY26NOV25000CE', executionInstrumentType: 'Option', executionExpiry: null,
        executionStrike: 25000, executionLotSize: 75, executionMaximumLots: 5, executionProposedEntry: 100,
        executionOneLotRisk: 375, executionStopLoss: 95, executionTarget: 110, executionRiskAmount: 1875,
        executionCapitalExposure: 37500, rejectionReasons: [], lifecycleStatus: 'Active', currentOptionPrice: 102,
        exitPrice: null, realisedPnl: null, unrealisedPnl: 2000, entryTimeUtc: bars[0].openTimeUtc, exitTimeUtc: null,
        maximumFavourablePnl: null, maximumAdverseLoss: null,
      };
      const previous = snapshot(bars, { overlays: [trade] });
      previous.paperAutomation.unrealisedPnl = 2000;
      const current = snapshot(bars, { overlays: [{ ...trade, ...changes }] });
      const alerts = buildMarketNarratorEvents('nifty', previous, current).filter(e => e.key.includes('profit-giveback:'));
      expect(alerts).toHaveLength(expected ? 1 : 0);
      if (expected) {
        expect(alerts[0].message).toContain(text);
        expect(alerts[0].alertText).toContain(changes.unrealisedPnl! > 0 ? 'Still green' :
          changes.unrealisedPnl === 0 ? 'Breakeven' : 'Now in loss');
      }
      current.isFresh = false;
      expect(buildMarketNarratorEvents('nifty', previous, current).some(e => e.key.includes('profit-giveback:'))).toBe(false);
    });

  it('announces completed-candle EMA and VWAP crossings', () => {
    const bars = [
      candle('2026-10-12T03:45:00Z', 99, 100, 98, 99),
      candle('2026-10-12T03:50:00Z', 99, 100, 98, 99),
      candle('2026-10-12T03:55:00Z', 99, 102, 99, 101, 220),
    ];
    const events = buildMarketNarratorEvents('nifty', snapshot(bars.slice(0, 2)), snapshot(bars));
    expect(events.some((event) => event.message.includes('closed above VWAP'))).toBe(true);
    expect(events.some((event) => event.message.includes('bullish EMA 9 and EMA 21 crossover'))).toBe(true);
    expect(events.find(event => event.key.includes('ema-bull:'))?.alertText).toBe('N · EMA 9 crossed above EMA 21');
  });

  it('does not evaluate analytical alerts from sub-five-minute bars', () => {
    const bars = [
      { ...candle('2026-10-12T03:45:00Z', 99, 100, 98, 99), intervalSeconds: 60 },
      { ...candle('2026-10-12T03:46:00Z', 99, 100, 98, 99), intervalSeconds: 60 },
      { ...candle('2026-10-12T03:47:00Z', 99, 102, 99, 101, 220), intervalSeconds: 60 },
    ];
    expect(buildMarketNarratorEvents('nifty', snapshot(bars.slice(0, 2)), snapshot(bars))).toEqual([]);
  });

  it('selects only the strongest confirmed market signal from one closed candle', () => {
    const bars = [
      candle('2026-10-12T03:45:00Z', 99, 100, 98, 99),
      candle('2026-10-12T03:50:00Z', 99, 100, 98, 99),
      candle('2026-10-12T03:55:00Z', 99, 103, 99, 102.5, 300),
    ];
    const all = buildMarketNarratorEvents('nifty', snapshot(bars.slice(0, 2)), snapshot(bars));
    const selected = selectHighSignalNarratorEvents(all);
    expect(all.length).toBeGreaterThan(1);
    expect(selected).toHaveLength(1);
    expect(selected[0].category).not.toBe('levels');
  });

  it('announces trade entries and exits once the lifecycle changes', () => {
    const bars = [candle('2026-10-12T03:45:00Z', 99, 100, 98, 99)];
    const active = { signalId: 's1', strategy: 'test', direction: 'Buy', signalTimeUtc: bars[0].openTimeUtc,
      entry: 100, stopLoss: 95, target: 110, status: 'Active', quantity: 5, fillPrice: 100,
      executionInstrument: 'NIFTY26OCT25000CE', executionInstrumentType: 'Option', executionExpiry: null,
      executionStrike: 25000, executionLotSize: 75, executionMaximumLots: 5, executionProposedEntry: 100,
      executionOneLotRisk: 375, executionStopLoss: 95, executionTarget: 110, executionRiskAmount: 1875,
      executionCapitalExposure: 37500, rejectionReasons: [], lifecycleStatus: 'Active', currentOptionPrice: 102,
      exitPrice: null, realisedPnl: null, unrealisedPnl: 750, entryTimeUtc: bars[0].openTimeUtc, exitTimeUtc: null,
      maximumFavourablePnl: null, maximumAdverseLoss: null };
    const exited = { ...active, lifecycleStatus: 'Target hit', exitPrice: 110, realisedPnl: 3750, unrealisedPnl: 0, exitTimeUtc: '2026-10-12T04:00:00Z' };
    const previous = snapshot(bars, { overlays: [active] });
    const current = snapshot(bars, { overlays: [exited] });
    const events = buildMarketNarratorEvents('nifty', previous, current);
    expect(events.some((event) => event.message.includes('target or profit objective reached'))).toBe(true);
    expect(events.find(event => event.key.includes('exit:'))?.alertText).toBe('N · 25000 CE · Target hit · Exit ₹110.00 · +₹3,750');
  });

  it.each([
    ['nifty', 'NIFTY26OCT25000CE', 'CallOption', 25000, 'bullish call-side', 'N · 25000 CE'],
    ['sensex', 'SENSEX26OCT73500PE', 'PutOption', 73500, 'bearish put-side', 'S · 73500 PE'],
    ['bank-nifty', 'BANKNIFTY26NOV61200PE', 'PutOption', 61200, 'bearish put-side', 'BN · 61200 PE'],
  ] as const)('shows compact alerts for %s without changing voice bias', (market, contract, type, strike, expectedBias, label) => {
    const bars = [candle('2026-10-12T03:45:00Z', 99, 100, 98, 99)];
    const trade = { signalId: 'option-side', strategy: 'test', direction: 'Buy', signalTimeUtc: bars[0].openTimeUtc,
      entry: 100, stopLoss: 95, target: 110, status: 'Active', quantity: 5, fillPrice: 837.60,
      executionInstrument: contract, executionInstrumentType: type, executionExpiry: null,
      executionStrike: strike, executionLotSize: 75, executionMaximumLots: 5, executionProposedEntry: 100,
      executionOneLotRisk: 375, executionStopLoss: 753.85, executionTarget: 110, executionRiskAmount: 1875,
      executionCapitalExposure: 37500, rejectionReasons: [], lifecycleStatus: 'Active', currentOptionPrice: 102,
      exitPrice: null, realisedPnl: null, unrealisedPnl: 750, entryTimeUtc: bars[0].openTimeUtc, exitTimeUtc: null,
      maximumFavourablePnl: null, maximumAdverseLoss: null };
    const events = buildMarketNarratorEvents(market, snapshot(bars), snapshot(bars, { overlays: [trade] }));
    const entry = events.find(event => event.key.includes('entry:'));
    expect(entry?.message).toContain(expectedBias);
    expect(entry?.alertText).toBe(`${label} · E ₹837.60 · SL ₹753.85`);
  });

  it('shortens a feed alert without dropping its diagnostic evidence', () => {
    const bars = [candle('2026-10-12T03:45:00Z', 99, 100, 98, 99)];
    const current = snapshot(bars, { isFresh: false, feedStatus: 'DataStale' });
    const events = selectHighSignalNarratorEvents(buildMarketNarratorEvents('sensex', snapshot(bars), current));
    expect(events[0].alertText).toBe('S · Feed stale or offline');
    expect(events[0].evidence).toBe('DataStale');
  });

  it('does not narrate every trailing-stop adjustment after profit is protected', () => {
    const bars = [candle('2026-10-12T03:45:00Z', 99, 100, 98, 99)];
    const trade = { signalId: 's1', strategy: 'test', direction: 'Buy', signalTimeUtc: bars[0].openTimeUtc,
      entry: 100, stopLoss: 101, target: 110, status: 'Active', quantity: 5, fillPrice: 100,
      executionInstrument: 'NIFTY26OCT25000CE', executionInstrumentType: 'Option', executionExpiry: null,
      executionStrike: 25000, executionLotSize: 75, executionMaximumLots: 5, executionProposedEntry: 100,
      executionOneLotRisk: 375, executionStopLoss: 95, executionTarget: 110, executionRiskAmount: 1875,
      executionCapitalExposure: 37500, rejectionReasons: [], lifecycleStatus: 'Active', currentOptionPrice: 103,
      exitPrice: null, realisedPnl: null, unrealisedPnl: 1000, entryTimeUtc: bars[0].openTimeUtc, exitTimeUtc: null,
      maximumFavourablePnl: null, maximumAdverseLoss: null };
    const previous = snapshot(bars, { overlays: [{ ...trade, stopLoss: 100.5 }] });
    const current = snapshot(bars, { overlays: [trade] });
    expect(buildMarketNarratorEvents('nifty', previous, current).some((event) => event.key.includes('profit-lock'))).toBe(false);
  });
});
