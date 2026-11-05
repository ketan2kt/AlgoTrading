import { TradingWorkspaceSnapshot } from './trading-workspace';
import { sessionKey } from './chart-overlays';

export function indexChange(view: TradingWorkspaceSnapshot) {
  const observed = Date.parse(view.observedAtUtc);
  if (!Number.isFinite(observed)) return null;
  const today = sessionKey(view.observedAtUtc);
  const candle = view.candles.filter(c => Number.isFinite(c.close) && c.close > 0 &&
    Date.parse(c.openTimeUtc) <= observed && sessionKey(c.openTimeUtc) === today)
    .sort((a, b) => Date.parse(a.openTimeUtc) - Date.parse(b.openTimeUtc)).at(-1);
  const previous = view.previousSessionClose;
  const previousClose = previous && previous.sessionDate < today && Number.isFinite(previous.price) && previous.price > 0
    ? previous.price : null;
  const price = candle?.close ?? null;
  const points = price !== null && previousClose !== null ? price - previousClose : null;
  return { price, previousClose, previousDate: previous?.sessionDate, points,
    percent: points !== null && previousClose !== null ? points / previousClose * 100 : null,
    fresh: view.isLive && view.isFresh && price !== null && !candle?.isClosed };
}
