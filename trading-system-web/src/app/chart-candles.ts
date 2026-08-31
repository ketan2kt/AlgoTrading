import { WorkspaceCandle, WorkspaceVolumeBar } from './trading-workspace';

export interface ChartLogicalRange {
  from: number;
  to: number;
}

export interface ChartTimeRange {
  from: number;
  to: number;
}

const IST_OFFSET_MILLISECONDS = 330 * 60_000;
const DEFAULT_SESSION_MINUTES = 375;

export function currentSessionLogicalRange(
  candles: WorkspaceCandle[],
  timeframeMinutes: number,
  sessionMinutes = DEFAULT_SESSION_MINUTES,
  auxiliaryTimesUtc: string[] = [],
): ChartLogicalRange | null {
  if (!candles.length || timeframeMinutes < 1) return null;

  const latestSession = istSessionDate(candles[candles.length - 1].openTimeUtc);
  // Lightweight Charts assigns logical indexes over the union of timestamps
  // from every series. Futures-volume bars can contain timestamps that the
  // spot/index candle series does not, so using only candle indexes shifts and
  // stretches the viewport differently for each market.
  const timeline = [...new Set([
    ...candles.map((candle) => candle.openTimeUtc),
    ...auxiliaryTimesUtc,
  ].map((time) => new Date(time).getTime()))].sort((left, right) => left - right);
  const firstCurrentSessionIndex = timeline.findIndex(
    (timestamp) => istSessionDate(new Date(timestamp).toISOString()) === latestSession,
  );
  if (firstCurrentSessionIndex < 0) return null;

  const expectedSessionBars = Math.ceil(sessionMinutes / timeframeMinutes);
  return {
    from: firstCurrentSessionIndex - 0.5,
    to: Math.max(candles.length + 1, firstCurrentSessionIndex + expectedSessionBars - 0.5),
  };
}

export function latestIstSessionDate(candles: WorkspaceCandle[]): string | null {
  return candles.length ? istSessionDate(candles[candles.length - 1].openTimeUtc) : null;
}

export function filterIstSession<T extends { openTimeUtc: string }>(
  values: T[],
  sessionDate: string | null,
): T[] {
  if (sessionDate === null) return [];
  return values.filter((value) => istSessionDate(value.openTimeUtc) === sessionDate);
}

export function currentSessionTimeRange(
  candles: WorkspaceCandle[],
  sessionMinutes = DEFAULT_SESSION_MINUTES,
): ChartTimeRange | null {
  if (!candles.length) return null;

  const latestSession = istSessionDate(candles[candles.length - 1].openTimeUtc);
  const first = candles.find((candle) => istSessionDate(candle.openTimeUtc) === latestSession);
  if (!first) return null;

  const from = Math.floor(new Date(first.openTimeUtc).getTime() / 1000);
  return { from, to: from + sessionMinutes * 60 };
}

export function istSessionDate(openTimeUtc: string): string {
  const timestamp = new Date(openTimeUtc).getTime() + IST_OFFSET_MILLISECONDS;
  return new Date(timestamp).toISOString().slice(0, 10);
}

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

export function aggregateVolumeBars(
  bars: WorkspaceVolumeBar[],
  timeframeMinutes: number,
): WorkspaceVolumeBar[] {
  if (timeframeMinutes <= 1) return bars;

  const bucketMilliseconds = timeframeMinutes * 60_000;
  const buckets = new Map<number, WorkspaceVolumeBar>();
  for (const bar of bars) {
    const timestamp = new Date(bar.openTimeUtc).getTime();
    const bucket = Math.floor(timestamp / bucketMilliseconds) * bucketMilliseconds;
    const current = buckets.get(bucket);
    buckets.set(bucket, {
      openTimeUtc: new Date(bucket).toISOString(),
      volume: (current?.volume ?? 0) + bar.volume,
      isClosed: bar.isClosed,
    });
  }
  return [...buckets.values()].sort(
    (left, right) => new Date(left.openTimeUtc).getTime() - new Date(right.openTimeUtc).getTime(),
  );
}
