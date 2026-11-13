import { chartLevels, ema, sessionVwap } from './chart-overlays';
import { chartEntryFills } from './chart-entry-fills';
import { mergeChartEntries, WorkspaceCandle, WorkspaceChartEntry, WorkspaceTradeOverlay } from './trading-workspace';
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
  it('keeps CE and PE on the actual entry candle after closure without marking the exit',()=>{
    const bar={...candle('2026-10-28T04:00:00Z',100,110,90,102),intervalSeconds:300};
    const next={...bar,openTimeUtc:'2026-10-28T04:05:00Z'};
    const trade={signalId:'p',direction:'Buy',entryTimeUtc:'2026-10-28T04:04:59Z',signalTimeUtc:'2026-10-28T03:55:00Z',
      executionInstrument:'54700PE',fillPrice:180,lifecycleStatus:'Active',exitTimeUtc:null} as WorkspaceTradeOverlay;
    const active=[trade,{...trade,signalId:'c',executionInstrument:'54700CE'}];
    const closed=active.map(row=>({...row,exitTimeUtc:'2026-10-28T04:05:15Z',lifecycleStatus:'Target hit'}));
    const fills=chartEntryFills(closed,[bar,next]);
    expect(fills).toEqual(chartEntryFills(active,[bar,next]));
    expect(fills.map(f=>f.side)).toEqual(['CE','PE']);
    expect(fills.every(f=>f.time===Date.parse(bar.openTimeUtc)/1000)).toBe(true);
    expect(chartEntryFills(closed,[next])).toEqual([]);
    expect(chartEntryFills(closed.map(row=>({...row,fillPrice:null})),[bar,next])).toEqual([]);
  });

  it('restores older entries from saved receipts, deduplicates, and prefers current entry details',()=>{
    const earlier=candle('2026-10-27T04:00:00Z',100,110,90,102);
    const later=candle('2026-10-28T04:00:00Z',100,110,90,102);
    const previous: WorkspaceChartEntry={tradeId:'older',side:'PE',contract:'54700PE',
      entryTimeUtc:'2026-10-27T04:00:10Z',entryPrice:181};
    const current: WorkspaceChartEntry={...previous,tradeId:'current',side:'CE',contract:'54700CE',
      entryTimeUtc:'2026-10-28T04:00:20Z',entryPrice:140};
    const history=mergeChartEntries([previous,current],[{...current,entryPrice:141}]);
    const trade={signalId:'current',entryTimeUtc:current.entryTimeUtc,executionInstrument:current.contract,
      fillPrice:142,entryEvidence:['Retest held']} as WorkspaceTradeOverlay;
    expect(history.map(row=>row.entryPrice)).toEqual([181,141]);
    expect(chartEntryFills([trade],[earlier,later],history).map(f=>[f.tradeId,f.entryPrice,f.evidence]))
      .toEqual([['older',181,undefined],['current',142,['Retest held']]]);
    expect(chartEntryFills([trade],[later],history).map(f=>f.tradeId)).toEqual(['current']);
  });
});
