import { TradingWorkspaceSnapshot, WorkspaceCandle } from './trading-workspace';

export interface DecisionMapView {
  regime: string;
  bias: string;
  setup: string;
  confidence: number;
  location: string;
  remainingRoom: string;
  volume: string;
  timeframes: { label: string; direction: string }[];
  reasons: string[];
}

export function buildDecisionMap(snapshot: TradingWorkspaceSnapshot): DecisionMapView | null {
  const session = latestSession(snapshot.candles);
  if (session.length < 15) return null;
  const latest = session.at(-1)!;
  const high = Math.max(...session.map(x => x.high));
  const low = Math.min(...session.map(x => x.low));
  const width = Math.max(high - low, Number.EPSILON);
  const position = (latest.close - low) / width;
  const evaluation = snapshot.evaluations.at(0);
  const directions = [1, 5, 15].map(minutes => ({
    label: `${minutes}m`, direction: trend(aggregate(session, minutes)),
  }));
  const agreeing = directions.filter(x => x.direction === 'Bullish').length >= 2 ? 'Bullish' :
    directions.filter(x => x.direction === 'Bearish').length >= 2 ? 'Bearish' : 'Mixed';
  const location = position <= .22 ? 'Lower range edge' : position >= .78 ? 'Upper range edge' : 'Range middle';
  const atr = averageRange(session.slice(-15));
  const nearest = agreeing === 'Bullish' ? high - latest.close : agreeing === 'Bearish' ? latest.close - low : 0;
  const room = atr > 0 ? nearest / atr : 0;
  const failed = evaluation?.failedConditions ?? [];
  return {
    regime: evaluation?.regime || inferRegime(session),
    bias: evaluation?.regimeBias || agreeing,
    setup: evaluation?.outcome || 'Scanning',
    confidence: evaluation?.regimeConfidence ?? 0,
    location,
    remainingRoom: agreeing === 'Mixed' ? 'Direction unresolved' : `${room.toFixed(2)} ATR to session edge`,
    volume: evaluation && evaluation.relativeFuturesVolume > 0
      ? `${evaluation.relativeFuturesVolume.toFixed(2)}× relative futures volume`
      : 'Futures volume confirmation unavailable',
    timeframes: directions,
    reasons: failed.length ? failed.slice(0, 3) : [
      `${agreeing} multi-timeframe alignment.`,
      `Price is ${(position * 100).toFixed(0)}% through today’s range.`,
    ],
  };
}

function latestSession(candles: WorkspaceCandle[]): WorkspaceCandle[] {
  if (!candles.length) return [];
  const date = istDate(candles.at(-1)!.openTimeUtc);
  return candles.filter(x => istDate(x.openTimeUtc) === date);
}

function istDate(value: string): string {
  return new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Kolkata', year:'numeric', month:'2-digit', day:'2-digit' }).format(new Date(value));
}

function aggregate(source: WorkspaceCandle[], minutes: number): WorkspaceCandle[] {
  if (minutes === 1) return source;
  const buckets = new Map<number, WorkspaceCandle[]>();
  for (const candle of source) {
    const time = new Date(candle.openTimeUtc).getTime();
    const key = Math.floor(time / (minutes * 60_000));
    const values = buckets.get(key) ?? [];
    values.push(candle); buckets.set(key, values);
  }
  return [...buckets.values()].map(values => ({ ...values[0], high:Math.max(...values.map(x=>x.high)), low:Math.min(...values.map(x=>x.low)), close:values.at(-1)!.close, volume:values.reduce((sum,x)=>sum+x.volume,0) }));
}

function trend(source: WorkspaceCandle[]): string {
  if (source.length < 4) return 'Waiting';
  const closes = source.map(x => x.close);
  const fast = ema(closes, Math.min(5, closes.length));
  const slow = ema(closes, Math.min(10, closes.length));
  const slope = closes.at(-1)! - closes[Math.max(0, closes.length - 4)];
  return fast > slow && slope > 0 ? 'Bullish' : fast < slow && slope < 0 ? 'Bearish' : 'Neutral';
}

function ema(values: number[], period: number): number {
  const k=2/(period+1); return values.slice(1).reduce((result,value)=>value*k+result*(1-k),values[0]);
}

function averageRange(values: WorkspaceCandle[]): number {
  return values.length ? values.reduce((sum,x)=>sum+x.high-x.low,0)/values.length : 0;
}

function inferRegime(values: WorkspaceCandle[]): string {
  const changes=values.slice(1).map((x,i)=>x.close-values[i].close);
  const travelled=changes.reduce((sum,x)=>sum+Math.abs(x),0);
  const efficiency=travelled ? Math.abs(values.at(-1)!.close-values[0].close)/travelled : 0;
  const flips=changes.slice(1).filter((x,i)=>Math.sign(x)!==Math.sign(changes[i])).length/Math.max(1,changes.length-1);
  return efficiency>.52?'Directional trend':flips>.58?'Noisy chop':efficiency<.3?'Structured range':'Transition';
}
