import { sessionKey } from './chart-overlays';
import { TradingWorkspaceSnapshot } from './trading-workspace';

export const deskMarkets = ['nifty', 'sensex', 'bank-nifty'] as const;

export function activeTradeCount(view?: TradingWorkspaceSnapshot): number {
  return new Set((view?.overlays ?? []).filter(t => t.fillPrice != null && !t.exitTimeUtc &&
    (t.lifecycleStatus.startsWith('Active') || t.lifecycleStatus === 'Entry filled')).map(t => t.signalId)).size;
}

export function deskPortfolio(views: ReadonlyMap<string, TradingWorkspaceSnapshot>, now = Date.now()) {
  const today = sessionKey(new Date(now).toISOString());
  const current = deskMarkets.map(m => views.get(m)).filter((v): v is TradingWorkspaceSnapshot =>
    !!v && Number.isFinite(Date.parse(v.observedAtUtc)) && sessionKey(v.observedAtUtc) === today &&
    Date.parse(v.observedAtUtc) <= now &&
    Number.isFinite(Date.parse(v.paperAutomation.observedAtUtc)) && sessionKey(v.paperAutomation.observedAtUtc) === today &&
    Number.isFinite(v.paperAutomation.realisedPnl) && Number.isFinite(v.paperAutomation.unrealisedPnl));
  const complete = current.length === deskMarkets.length;
  return { complete, count:current.length,
    total:complete ? current.reduce((sum,v)=>sum+v.paperAutomation.realisedPnl+v.paperAutomation.unrealisedPnl,0) : null,
    stale:current.some(v=>!v.isFresh || now-Date.parse(v.observedAtUtc)>15000 ||
      now-Date.parse(v.paperAutomation.observedAtUtc)>15000) };
}
