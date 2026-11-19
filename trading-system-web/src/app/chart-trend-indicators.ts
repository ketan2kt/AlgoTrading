import {aggregateCandles, istSessionDate} from './chart-candles';
import {WorkspaceCandle} from './trading-workspace';

export interface TrendPoint {
  openTimeUtc: string;
  support?: number; resistance?: number;
  adx?: number; plus?: number; minus?: number;
  chandelierUp?: number; chandelierDown?: number;
  structure: string;
}
export interface StructureMark {openTimeUtc: string; text: string; above: boolean;}

// Reuse chart aggregation, but require every constituent candle to be closed,
// valid and contiguous. Incomplete buckets cannot confirm a pivot or indicator.
export function closedIndicatorCandles(source: WorkspaceCandle[], minutes: number): WorkspaceCandle[] {
  const width = minutes * 60000;
  if (!Number.isFinite(width) || width <= 0) return [];
  const groups = new Map<number, WorkspaceCandle[]>();
  for (const bar of source) {
    const stamp = Date.parse(bar.openTimeUtc);
    if (!Number.isFinite(stamp)) continue;
    const key = Math.floor(stamp / width) * width;
    groups.set(key, [...(groups.get(key) ?? []), bar]);
  }
  const valid: WorkspaceCandle[] = [];
  for (const [key, values] of groups) {
    const bars = values.sort((a,b)=>Date.parse(a.openTimeUtc)-Date.parse(b.openTimeUtc));
    let cursor = key;
    const complete = bars.every(b => {
      if (!b.isClosed || Date.parse(b.openTimeUtc) !== cursor || b.intervalSeconds <= 0 ||
          ![b.open,b.high,b.low,b.close,b.intervalSeconds].every(Number.isFinite) ||
          b.low <= 0 || b.high < Math.max(b.open,b.close,b.low) || b.low > Math.min(b.open,b.close)) return false;
      cursor += b.intervalSeconds * 1000;
      return cursor <= key + width;
    });
    if (complete && cursor === key + width) valid.push(...bars);
  }
  return aggregateCandles(valid.sort((a,b)=>Date.parse(a.openTimeUtc)-Date.parse(b.openTimeUtc)), minutes);
}

// Display-only Wilder ADX/DMI(14), Chandelier(22,3), and 2-left/2-right swings.
// Every result is published on its confirmation candle, never backdated.
export function chartTrendIndicators(bars: WorkspaceCandle[]): {points: TrendPoint[]; marks: StructureMark[]} {
  const points: TrendPoint[] = [], marks: StructureMark[] = [];
  let segment: WorkspaceCandle[] = [];
  let tr14 = 0, plus14 = 0, minus14 = 0, atr22 = 0, dxSum = 0, dxCount = 0;
  let adx: number | undefined, support: number | undefined, resistance: number | undefined;
  let highLabel = '', lowLabel = '', supportBroken = false, resistanceBroken = false;
  for (const bar of bars) {
    const prior = segment.at(-1);
    const at = Date.parse(bar.openTimeUtc);
    const nextSession = prior && istSessionDate(prior.openTimeUtc) !== istSessionDate(bar.openTimeUtc) &&
      new Date(Date.parse(prior.openTimeUtc) + prior.intervalSeconds * 1000 + 330 * 60000).toISOString().slice(11,16) === '15:30' &&
      new Date(at + 330 * 60000).toISOString().slice(11,16) === '09:15';
    if (prior && (bar.intervalSeconds !== prior.intervalSeconds ||
        at !== Date.parse(prior.openTimeUtc) + prior.intervalSeconds * 1000 && !nextSession)) {
      segment = []; tr14 = plus14 = minus14 = atr22 = dxSum = dxCount = 0;
      adx = support = resistance = undefined; highLabel = lowLabel = '';
      supportBroken = resistanceBroken = false;
    }
    const previous = segment.at(-1);
    segment.push(bar);
    const i = segment.length - 1;
    const point: TrendPoint = {openTimeUtc: bar.openTimeUtc, structure: 'Waiting for swings'};
    if (previous) {
      const tr = Math.max(bar.high-bar.low, Math.abs(bar.high-previous.close), Math.abs(bar.low-previous.close));
      const up = bar.high-previous.high, down = previous.low-bar.low;
      const plus = up > down && up > 0 ? up : 0, minus = down > up && down > 0 ? down : 0;
      tr14 = i <= 14 ? tr14 + tr : tr14 - tr14/14 + tr;
      plus14 = i <= 14 ? plus14 + plus : plus14 - plus14/14 + plus;
      minus14 = i <= 14 ? minus14 + minus : minus14 - minus14/14 + minus;
      atr22 = i <= 22 ? atr22 + tr/22 : (atr22*21+tr)/22;
      if (i >= 14) {
        point.plus = tr14 > 0 ? 100*plus14/tr14 : 0;
        point.minus = tr14 > 0 ? 100*minus14/tr14 : 0;
        const total = point.plus + point.minus;
        const dx = total > 0 ? 100*Math.abs(point.plus-point.minus)/total : 0;
        if (dxCount < 14) { dxSum += dx; dxCount++; if (dxCount === 14) adx = dxSum/14; }
        else adx = (adx!*13+dx)/14;
        point.adx = adx;
      }
      if (i >= 22) {
        const window = segment.slice(-22);
        point.chandelierUp = Math.max(...window.map(b=>b.high)) - 3*atr22;
        point.chandelierDown = Math.min(...window.map(b=>b.low)) + 3*atr22;
      }
    }
    if (segment.length >= 5) {
      const window = segment.slice(-5), pivot = window[2];
      const others = window.filter((_, index)=>index !== 2);
      if (others.every(b=>b.high < pivot.high)) {
        highLabel = resistance === undefined ? 'H' : pivot.high > resistance ? 'HH' : pivot.high < resistance ? 'LH' : 'EH';
        resistance = pivot.high; resistanceBroken = false;
        marks.push({openTimeUtc: bar.openTimeUtc, text: highLabel, above: true});
      }
      if (others.every(b=>b.low > pivot.low)) {
        lowLabel = support === undefined ? 'L' : pivot.low > support ? 'HL' : pivot.low < support ? 'LL' : 'EL';
        support = pivot.low; supportBroken = false;
        marks.push({openTimeUtc: bar.openTimeUtc, text: lowLabel, above: false});
      }
    }
    point.support = support; point.resistance = resistance;
    point.structure = [highLabel,lowLabel].filter(Boolean).join('/') || point.structure;
    if (support !== undefined && bar.close < support) {
      point.structure += ' · support broken';
      if (!supportBroken) marks.push({openTimeUtc: bar.openTimeUtc, text: 'Break ↓', above: true});
      supportBroken = true;
    }
    if (resistance !== undefined && bar.close > resistance) {
      point.structure += ' · resistance broken';
      if (!resistanceBroken) marks.push({openTimeUtc: bar.openTimeUtc, text: 'Break ↑', above: false});
      resistanceBroken = true;
    }
    points.push(point);
  }
  return {points, marks};
}
