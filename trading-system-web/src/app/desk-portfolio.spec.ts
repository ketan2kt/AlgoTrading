import { describe, expect, it } from 'vitest';
import { activeTradeCount, deskPortfolio } from './desk-portfolio';
import { TradingWorkspaceSnapshot, WorkspaceTradeOverlay } from './trading-workspace';

const now=Date.parse('2026-11-05T06:00:00Z');
const view=(realised:number,open:number):TradingWorkspaceSnapshot=>({
  observedAtUtc:new Date(now).toISOString(),isFresh:true,overlays:[],
  paperAutomation:{realisedPnl:realised,unrealisedPnl:open,observedAtUtc:new Date(now).toISOString()}
} as unknown as TradingWorkspaceSnapshot);

describe('three-market desk summary',()=>{
  it('sums realised and open P&L exactly once across the three indexes',()=>{
    const views=new Map([['nifty',view(1000,200)],['sensex',view(-400,100)],['bank-nifty',view(500,-50)],['bankex',view(999,999)]]);
    expect(deskPortfolio(views,now)).toMatchObject({total:1350,complete:true,stale:false});
    views.set('sensex',view(600,0));
    expect(deskPortfolio(views,now).total).toBe(2250);
  });
  it('never labels a partial or mixed-day subtotal as the overall result',()=>{
    const views=new Map([['nifty',view(1000,200)],['sensex',view(-400,100)]]);
    expect(deskPortfolio(views,now)).toMatchObject({total:null,count:2,complete:false});
    views.set('bank-nifty',{...view(500,0),observedAtUtc:'2026-11-04T06:00:00Z'});
    expect(deskPortfolio(views,now).total).toBeNull();
    views.set('bank-nifty',{...view(500,0),observedAtUtc:'2026-11-05T05:59:00Z'});
    expect(deskPortfolio(views,now)).toMatchObject({total:1400,stale:true});
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
