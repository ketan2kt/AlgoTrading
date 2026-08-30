import { chartLevels, ema, entryExplanation, sessionVwap } from './chart-overlays';
import { WorkspaceCandle, WorkspaceTradeOverlay } from './trading-workspace';
const candle=(time:string,open:number,high:number,low:number,close:number,volume=10):WorkspaceCandle=>({openTimeUtc:time,intervalSeconds:60,open,high,low,close,volume,isClosed:true});
describe('automatic chart overlays',()=>{
  it('calculates current, previous and opening-range levels in IST sessions',()=>{const result=chartLevels([
    candle('2026-08-28T03:45:00Z',100,105,99,104),candle('2026-08-28T09:59:00Z',104,108,103,107),
    candle('2026-08-30T03:45:00Z',110,112,109,111),candle('2026-08-30T03:59:00Z',111,114,110,113),candle('2026-08-30T04:00:00Z',113,116,112,115)])!;
    expect(result.previousHigh).toBe(108);expect(result.previousLow).toBe(99);expect(result.previousClose).toBe(107);expect(result.dayHigh).toBe(116);expect(result.openingRangeHigh).toBe(114);});
  it('calculates EMA and resets VWAP each session',()=>{const candles=[candle('2026-08-28T03:45:00Z',100,102,98,101,10),candle('2026-08-30T03:45:00Z',110,112,108,111,20)];expect(ema(candles,9).length).toBe(2);expect(sessionVwap(candles)[1].value).toBeCloseTo((112+108+111)/3,6);});
  it('explains entries from matching deterministic evaluations',()=>{const overlay={signalId:'s1',strategy:'ORB',direction:'Buy',status:'Filled'} as WorkspaceTradeOverlay;const text=entryExplanation(overlay,[{signalId:'s1',currentPrice:110,vwap:105,fastEma:108,slowEma:106,openingRangeHigh:109,openingRangeLow:100} as never]);expect(text).toContain('above VWAP');expect(text).toContain('EMA9 > EMA21');expect(text).toContain('OR breakout');});
});
