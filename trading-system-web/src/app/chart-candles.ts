import { WorkspaceCandle } from './trading-workspace';

export function aggregateCandles(
  candles: WorkspaceCandle[],
  timeframeMinutes: number,
): WorkspaceCandle[] {
  if (timeframeMinutes <= 1) return candles;

  const bucketMilliseconds = timeframeMinutes * 60_000;
  const buckets = new Map<number, WorkspaceCandle>();
  for (const candle of candles) {
    const timestamp = new Date(candle.openTimeUtc).getTime();
    const bucket = Math.floor(timestamp / bucketMilliseconds) * bucketMilliseconds;
    const current = buckets.get(bucket);
    if (!current) {
      buckets.set(bucket, {
        ...candle,
        openTimeUtc: new Date(bucket).toISOString(),
        intervalSeconds: timeframeMinutes * 60,
      });
      continue;
    }

    buckets.set(bucket, {
      ...current,
      high: Math.max(current.high, candle.high),
      low: Math.min(current.low, candle.low),
      close: candle.close,
      volume: current.volume + candle.volume,
      isClosed: candle.isClosed,
    });
  }

  return [...buckets.values()].sort(
    (left, right) => new Date(left.openTimeUtc).getTime() - new Date(right.openTimeUtc).getTime(),
  );
}
