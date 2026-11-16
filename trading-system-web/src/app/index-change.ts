import { TradingWorkspaceSnapshot } from './trading-workspace';
import { sessionKey } from './chart-overlays';

export function indexChange(view: TradingWorkspaceSnapshot) {
  // Backup cache is applied after the workspace's database reads. Its quote
  // can legitimately be newer than the start-of-query snapshot timestamp.
  const observedAt = view.chartDisplay?.evaluatedAtUtc ?? view.observedAtUtc;
  const observed = Date.parse(observedAt);
  if (!Number.isFinite(observed)) return null;
  const today = sessionKey(observedAt);
  const candle = view.candles.filter(c => Number.isFinite(c.close) && c.close > 0 &&
    Date.parse(c.openTimeUtc) <= observed && sessionKey(c.openTimeUtc) === today)
    .sort((a, b) => Date.parse(a.openTimeUtc) - Date.parse(b.openTimeUtc)).at(-1);
  const previous = view.previousSessionClose;
  const previousClose = previous && previous.sessionDate < today && Number.isFinite(previous.price) && previous.price > 0
    ? previous.price : null;
  const backup = view.chartDisplay;
  const backupAt = backup?.timestampUtc ? Date.parse(backup.timestampUtc) : NaN;
  const validBackupPrice = Number.isFinite(backupAt) && backupAt <= observed &&
    sessionKey(backup!.timestampUtc!) === today && backup?.lastPrice != null && backup.lastPrice > 0;
  const price = validBackupPrice ? backup!.lastPrice : candle?.close ?? null;
  const points = price !== null && previousClose !== null ? price - previousClose : null;
  return { price, previousClose, previousDate: previous?.sessionDate, points,
    percent: points !== null && previousClose !== null ? points / previousClose * 100 : null,
    fresh: backup ? (backup.quoteIsFresh ?? backup.isFresh) && validBackupPrice && observed - backupAt <= 30_000 :
      view.isLive && view.isFresh && price !== null && !candle?.isClosed };
}
