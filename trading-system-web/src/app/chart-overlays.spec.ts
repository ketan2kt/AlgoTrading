import { chartLevels, ema, sessionVwap } from './chart-overlays';
import { activeFills } from './chart-active-fills';
import { WorkspaceCandle, WorkspaceTradeOverlay } from './trading-workspace';
const candle=(time:string,open:number,high:number,low:number,close:number,volume=10):WorkspaceCandle=>({openTimeUtc:time,intervalSeconds:60,open,high,low,close,volume,isClosed:true});
describe('automatic chart overlays',()=>{
  it('calculates current, previous and opening-range levels in IST sessions',()=>{const result=chartLevels([
    candle('2026-08-28T03:45:00Z',100,105,99,104),candle('2026-08-28T09:59:00Z',104,108,103,107),
    candle('2026-08-30T03:45:00Z',110,112,109,111),candle('2026-08-30T03:59:00Z',111,114,110,113),candle('2026-08-30T04:00:00Z',113,116,112,115)])!;
    expect(result.previousHigh).toBe(108);expect(result.previousLow).toBe(99);expect(result.previousClose).toBe(107);expect(result.dayHigh).toBe(116);expect(result.openingRangeHigh).toBeNull();});
  it('calculates EMA and resets VWAP each session',()=>{const candles=[candle('2026-08-28T03:45:00Z',100,102,98,101,10),candle('2026-08-30T03:45:00Z',110,112,108,111,20)];expect(ema(candles,9).length).toBe(2);expect(sessionVwap(candles)[1].value).toBeCloseTo((112+108+111)/3,6);});
  it('uses exactly 09:15–09:30 IST, not the first available or unfinished bars',()=>{
    const start=Date.parse('2026-10-28T03:45:00Z');
    const bars=Array.from({length:16},(_,i)=>candle(new Date(start+i*60000).toISOString(),100,105+i,90,103));
    expect(chartLevels(bars)?.openingRangeHigh).toBe(119);
    expect(chartLevels(bars.slice(1))?.openingRangeLow).toBeNull();
    expect(chartLevels(bars.map((b,i)=>({...b,isClosed:i!==14})))?.openingRangeHigh).toBeNull();
  });
  it('marks open PE and CE on the actual fill bar; never maps a missing candle or closed fill',()=>{
    const bar={...candle('2026-10-28T04:00:00Z',100,110,90,102),intervalSeconds:300};
    const trade={signalId:'p',direction:'Buy',entryTimeUtc:'2026-10-28T04:04:59Z',signalTimeUtc:'2026-10-28T03:55:00Z',
      executionInstrument:'54700PE',fillPrice:180,lifecycleStatus:'Active',exitTimeUtc:null} as WorkspaceTradeOverlay;
    const fills=activeFills([trade,{...trade,signalId:'c',executionInstrument:'54700CE'},
      {...trade,entryTimeUtc:'2026-10-28T04:05:00Z'},{...trade,exitTimeUtc:'2026-10-28T04:05:00Z'}],[bar]);
    expect(fills.map(f=>f.side)).toEqual(['CE','PE']);
    expect(fills.every(f=>f.time===Date.parse(bar.openTimeUtc)/1000)).toBe(true);
  });
});
