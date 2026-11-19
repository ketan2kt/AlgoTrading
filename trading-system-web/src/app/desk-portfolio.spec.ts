import { describe, expect, it } from 'vitest';
import { activeTradeCount, CompletedDeskPortfolio } from './desk-portfolio';
import { TradingWorkspaceSnapshot, WorkspaceTradeOverlay } from './trading-workspace';

const now=Date.parse('2026-11-05T06:00:00Z');
const view=(realised:number,open:number):TradingWorkspaceSnapshot=>({
  observedAtUtc:new Date(now).toISOString(),isFresh:true,overlays:[],
  completedTradePnl:{tradingDate:'2026-11-05',netPnl:realised,tradeCount:1},
  paperAutomation:{realisedPnl:realised,unrealisedPnl:open,observedAtUtc:new Date(now).toISOString()}
} as unknown as TradingWorkspaceSnapshot);

describe('three-market desk summary',()=>{
  it('uses only saved completed net, ignoring open profit and partial realised amounts',()=>{
    const desk=new CompletedDeskPortfolio();
    desk.update('nifty',view(1000,200));desk.update('sensex',view(-400,100));desk.update('bank-nifty',view(500,-50));
    desk.update('bankex',view(999,999));
    expect(desk.read(now)).toMatchObject({total:1100,complete:true,stale:false});
    const running=view(-400,90000);running.paperAutomation.realisedPnl=8000;
    desk.update('sensex',running);
    expect(desk.read(now).total).toBe(1100);
    desk.update('sensex',view(600,0));
    expect(desk.read(now).total).toBe(2100);
  });
  it('never labels a partial or mixed-day subtotal as the overall result',()=>{
    const desk=new CompletedDeskPortfolio();desk.update('nifty',view(1000,200));desk.update('sensex',view(-400,100));
    expect(desk.read(now)).toMatchObject({total:null,count:2,complete:false});
    desk.update('bank-nifty',{...view(500,0),observedAtUtc:'2026-11-04T06:00:00Z'});
    expect(desk.read(now).total).toBeNull();
    desk.update('bank-nifty',{...view(500,0),observedAtUtc:'2026-11-05T05:57:00Z'});
    expect(desk.read(now)).toMatchObject({total:1100,stale:true});
    expect(desk.read(now+86400000).total).toBeNull();
    desk.clear();expect(desk.read(now).count).toBe(0);
  });
  it('retains the number through stale feeds, temporary empty snapshots and modest clock skew',()=>{
    const desk=new CompletedDeskPortfolio();
    for(const market of ['nifty','sensex','bank-nifty']) desk.update(market,{...view(100,0),isFresh:false,observedAtUtc:new Date(now+2000).toISOString()});
    expect(desk.read(now).total).toBe(300);
    desk.update('sensex',{...view(0,0),completedTradePnl:null});
    desk.update('nifty',{...view(-900,0),observedAtUtc:new Date(now-10000).toISOString()});
    expect(desk.read(now+200000)).toMatchObject({total:300,stale:true});
  });
  it('accepts a real zero-trade day and rejects invalid totals without erasing prior values',()=>{
    const desk=new CompletedDeskPortfolio();
    for(const market of ['nifty','sensex','bank-nifty']) desk.update(market,{...view(0,0),completedTradePnl:{tradingDate:'2026-11-05',netPnl:0,tradeCount:0}});
    expect(desk.read(now).total).toBe(0);
    desk.update('nifty',{...view(0,0),completedTradePnl:{tradingDate:'2026-11-05',netPnl:NaN,tradeCount:0}});
    expect(desk.read(now).total).toBe(0);
  });
  it('keeps the position count until an actual close, including partials and specialty legs',()=>{
    const active={signalId:'a',fillPrice:100,entryTimeUtc:'2026-11-05T05:00:00Z',exitTimeUtc:null,lifecycleStatus:'Active'} as WorkspaceTradeOverlay;
    const snapshot={...view(0,0),overlays:[active,{...active}, {...active,signalId:'b',strategy:'Hero Zero|CE'},
      {...active,signalId:'c',lifecycleStatus:'Active - partially exited'},
      {...active,signalId:'d',exitTimeUtc:'2026-11-05T05:30:00Z'},
      {...active,signalId:'e',fillPrice:null}]};
    expect(activeTradeCount(snapshot)).toBe(3);
    snapshot.overlays.forEach(t=>t.exitTimeUtc='2026-11-05T06:00:00Z');
    expect(activeTradeCount(snapshot)).toBe(0);
    expect(activeTradeCount()).toBe(0);
  });
});
