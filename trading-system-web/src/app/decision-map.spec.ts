import { buildDecisionMap } from './decision-map';
import { TradingWorkspaceSnapshot } from './trading-workspace';

describe('decision map', () => {
  it('exposes multi-timeframe and market-location context', () => {
    const candles = Array.from({length:30},(_,i)=>({openTimeUtc:new Date(Date.UTC(2026,9,1,3,45+i)).toISOString(),intervalSeconds:60,open:100+i,high:101+i,low:99+i,close:100.8+i,volume:100,isClosed:true}));
    const result=buildDecisionMap({instrument:'NIFTY',exchange:'NSE',timeframe:'1m',mode:'Paper',feedStatus:'Connected',isLive:true,isFresh:true,lastMarketTimestampUtc:null,observedAtUtc:'',statusMessage:null,candles,overlays:[],evaluations:[],paperAutomation:{} as never});
    expect(result?.bias).toBe('Bullish');
    expect(result?.timeframes).toHaveLength(3);
    expect(result?.location).toBe('Upper range edge');
  });
});
