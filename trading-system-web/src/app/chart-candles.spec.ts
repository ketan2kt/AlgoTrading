import { describe, expect, it } from 'vitest';
import { aggregateCandles } from './chart-candles';
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
