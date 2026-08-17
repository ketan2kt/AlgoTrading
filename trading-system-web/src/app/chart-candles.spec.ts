import { describe, expect, it } from 'vitest';
import { aggregateCandles, aggregateVolumeBars, currentSessionLogicalRange } from './chart-candles';
import { WorkspaceCandle } from './trading-workspace';

describe('aggregateCandles', () => {
  it('combines one-minute candles into aligned five-minute OHLC candles', () => {
    const candles: WorkspaceCandle[] = [
      candle('2026-08-03T03:45:00Z', 100, 104, 99, 102, 10),
      candle('2026-08-03T03:46:00Z', 102, 106, 101, 105, 20),
      candle('2026-08-03T03:49:00Z', 105, 107, 98, 99, 30),
    ];

    const result = aggregateCandles(candles, 5);

    expect(result).toHaveLength(1);
    expect(result[0]).toMatchObject({
      openTimeUtc: '2026-08-03T03:45:00.000Z',
      open: 100,
      high: 107,
      low: 98,
      close: 99,
      volume: 60,
    });
  });
});

describe('aggregateVolumeBars', () => {
  it('sums aligned one-minute futures volume into the selected timeframe', () => {
    const result = aggregateVolumeBars(
      [
        { openTimeUtc: '2026-08-17T03:45:00Z', volume: 100, isClosed: true },
        { openTimeUtc: '2026-08-17T03:46:00Z', volume: 125, isClosed: true },
        { openTimeUtc: '2026-08-17T03:50:00Z', volume: 80, isClosed: true },
      ],
      5,
    );

    expect(result).toEqual([
      { openTimeUtc: '2026-08-17T03:45:00.000Z', volume: 225, isClosed: true },
      { openTimeUtc: '2026-08-17T03:50:00.000Z', volume: 80, isClosed: true },
    ]);
  });
});

describe('currentSessionLogicalRange', () => {
  it('places the latest session first candle at the left and keeps prior sessions off-screen', () => {
    const candles: WorkspaceCandle[] = [
      candle('2026-08-11T03:45:00Z', 100, 101, 99, 100, 10),
      candle('2026-08-11T03:50:00Z', 100, 102, 100, 101, 10),
      candle('2026-08-12T03:45:00Z', 102, 103, 101, 102, 10),
      candle('2026-08-12T03:50:00Z', 102, 104, 102, 103, 10),
    ];

    expect(currentSessionLogicalRange(candles, 5)).toEqual({ from: 1.5, to: 76.5 });
  });
});

function candle(
  openTimeUtc: string,
  open: number,
  high: number,
  low: number,
  close: number,
  volume: number,
): WorkspaceCandle {
  return { openTimeUtc, intervalSeconds: 60, open, high, low, close, volume, isClosed: true };
}
