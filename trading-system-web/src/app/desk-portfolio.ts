import { sessionKey } from './chart-overlays';
import { TradingWorkspaceSnapshot } from './trading-workspace';

export const deskMarkets = ['nifty', 'sensex', 'bank-nifty'] as const;

export function activeTradeCount(view?: TradingWorkspaceSnapshot): number {
  return new Set((view?.overlays ?? []).filter(t => t.fillPrice != null && !t.exitTimeUtc &&
    (t.lifecycleStatus.startsWith('Active') || t.lifecycleStatus === 'Entry filled')).map(t => t.signalId)).size;
}

export class CompletedDeskPortfolio {
  private readonly saved = new Map<string,{day:string;net:number;observed:number}>();

  update(market:string, view:TradingWorkspaceSnapshot):void {
    if (!(deskMarkets as readonly string[]).includes(market)) return;
    const total=view.completedTradePnl, observed=Date.parse(view.observedAtUtc);
    if (!total || !Number.isFinite(observed) || total.tradingDate!==sessionKey(view.observedAtUtc) ||
        !Number.isFinite(total.netPnl) || !Number.isInteger(total.tradeCount) || total.tradeCount<0) return;
    const previous=this.saved.get(market);
    if (previous && observed<previous.observed) return;
    this.saved.set(market,{day:total.tradingDate,net:total.netPnl,observed});
  }

  clear():void { this.saved.clear(); }

  readMarket(market:string, now=Date.now()):number|null {
    const row=this.saved.get(market);
    return row?.day===sessionKey(new Date(now).toISOString()) ? row.net : null;
  }

  read(now=Date.now()) {
    const today=sessionKey(new Date(now).toISOString());
    const rows=[...this.saved.values()].filter(v=>v.day===today);
    const complete=rows.length===deskMarkets.length;
    return {complete,count:rows.length,total:complete?rows.reduce((sum,v)=>sum+v.net,0):null,
      stale:rows.some(v=>now-v.observed>120000)};
  }
}
