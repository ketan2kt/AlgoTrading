import {chartTrendIndicators, closedIndicatorCandles} from './chart-trend-indicators';
import {WorkspaceCandle} from './trading-workspace';

const start = Date.parse('2026-11-19T03:45:00Z');
const bars = (count: number, sign = 1): WorkspaceCandle[] => Array.from({length: count}, (_,i)=>({
  openTimeUtc:new Date(start+i*60000).toISOString(),intervalSeconds:60,
  open:100+sign*i,close:100+sign*i,high:101+sign*i,low:99+sign*i,volume:10,isClosed:true
}));

describe('display-only trend calculations',()=>{
  it('matches Wilder directional movement, warmup and Chandelier reference values',()=>{
    const result=chartTrendIndicators(bars(40));
    expect(result.points[13].plus).toBeUndefined();
    expect(result.points[14].plus).toBe(50);
    expect(result.points[14].minus).toBe(0);
    expect(result.points[26].adx).toBeUndefined();
    expect(result.points[27].adx).toBe(100);
    expect(result.points[21].chandelierUp).toBeUndefined();
    expect(result.points[22].chandelierUp).toBeCloseTo(117);
    expect(result.points[22].chandelierDown).toBeCloseTo(106);
    const falling=chartTrendIndicators(bars(40,-1)).points.at(-1)!;
    expect(falling.adx).toBe(100);expect(falling.plus).toBe(0);expect(falling.minus).toBe(50);
  });
  it('uses Wilder smoothing rather than replacing the window average',()=>{
    const input=bars(30);
    input[15]={...input[15],high:120,low:110};
    const point=chartTrendIndicators(input.slice(0,16)).points[15];
    // TR sum14:28 ->26+10=36; +DM sum14:14 ->13+5=18.
    expect(point.plus).toBeCloseTo(50);
    expect(point.minus).toBe(0);
  });
  it('handles flat markets without NaN or invented trend strength',()=>{
    const input=bars(40).map(b=>({...b,open:100,close:100,high:100,low:100}));
    const last=chartTrendIndicators(input).points.at(-1)!;
    expect(last.adx).toBe(0);expect(last.plus).toBe(0);expect(last.minus).toBe(0);
    expect(last.chandelierUp).toBe(100);expect(last.chandelierDown).toBe(100);
  });
  it('publishes swings only at confirmation and never rewrites earlier results with future bars',()=>{
    const input=bars(10).map((b,i)=>({...b,open:100,close:100,high:[101,102,110,103,102,101,102,112,103,102][i],low:99}));
    const before=chartTrendIndicators(input.slice(0,4));
    expect(before.marks).toEqual([]);
    const first=chartTrendIndicators(input.slice(0,5));
    expect(first.marks).toEqual([{openTimeUtc:input[4].openTimeUtc,text:'H',above:true}]);
    expect(first.points[3].resistance).toBeUndefined();
    expect(first.points[4].resistance).toBe(110);
    const full=chartTrendIndicators(input);
    expect(full.points.slice(0,5)).toEqual(first.points);
    expect(full.marks.at(-1)?.text).toBe('HH');
    expect(full.marks.at(-1)?.openTimeUtc).toBe(input[9].openTimeUtc);
  });
  it('marks a closed structure break once and retains the confirmed level',()=>{
    const input=bars(8).map((b,i)=>({...b,open:100,close:100,high:105,low:[99,98,90,97,98,99,99,99][i]}));
    input[5]={...input[5],open:89,close:89,low:88};
    input[6]={...input[6],open:88,close:88,low:87};
    const result=chartTrendIndicators(input.slice(0,7));
    expect(result.points[4].support).toBe(90);
    expect(result.points[5].structure).toContain('support broken');
    expect(result.marks.filter(m=>m.text==='Break ↓')).toHaveLength(1);
  });
  it('resets at an intraday gap but can warm up across a complete session boundary',()=>{
    const input=bars(70);input.splice(40,1);
    const result=chartTrendIndicators(input);
    expect(result.points[40].adx).toBeUndefined();
    expect(result.points[40].chandelierUp).toBeUndefined();
    expect(result.points.at(-1)?.adx).toBe(100);
    const yesterday=bars(30).map((b,i)=>({...b,openTimeUtc:new Date(Date.parse('2026-11-18T09:30:00Z')+i*60000).toISOString()}));
    expect(chartTrendIndicators([...yesterday,...bars(1)]).points.at(-1)?.adx).toBeDefined();
  });
});

describe('closed chart inputs',()=>{
  it('reuses aggregation only for complete closed buckets',()=>{
    const input=bars(11);
    expect(closedIndicatorCandles(input,5)).toHaveLength(2);
    expect(closedIndicatorCandles(input.filter((_,i)=>i!==2),5)).toHaveLength(1);
    expect(closedIndicatorCandles(input.map((b,i)=>({...b,isClosed:i!==3})),5)).toHaveLength(1);
    expect(closedIndicatorCandles([...input,input[2]],5)).toHaveLength(1);
    expect(closedIndicatorCandles(input.map((b,i)=>i===2?{...b,high:NaN}:b),5)).toHaveLength(1);
    expect(closedIndicatorCandles([...input].reverse(),5)).toEqual(closedIndicatorCandles(input,5));
    expect(closedIndicatorCandles(input,0)).toEqual([]);
  });
  it('does not mutate source candles or show an unfinished last minute',()=>{
    const input=bars(30).map((b,i)=>Object.freeze({...b,isClosed:i!==29}));
    const original=JSON.stringify(input);
    expect(chartTrendIndicators(closedIndicatorCandles(input,1)).points).toHaveLength(29);
    expect(JSON.stringify(input)).toBe(original);
  });
});
