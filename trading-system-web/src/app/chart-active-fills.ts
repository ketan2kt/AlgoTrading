import { WorkspaceCandle, WorkspaceTradeOverlay } from './trading-workspace';

export function activeFills(trades:WorkspaceTradeOverlay[], bars:WorkspaceCandle[]) {
  const fills = trades.flatMap(trade=>{
    if (trade.fillPrice == null || !trade.entryTimeUtc || trade.exitTimeUtc ||
        !(trade.lifecycleStatus.startsWith('Active') || trade.lifecycleStatus==='Entry filled')) return [];
    const side = trade.executionInstrument?.trim().toUpperCase().match(/(CE|PE)$/)?.[1] ??
      ({CallOption:'CE',PutOption:'PE'} as Record<string,string>)[trade.executionInstrumentType ?? ''];
    if (!side) return [];
    const at = Date.parse(trade.entryTimeUtc);
    const bar = bars.find(b=>{const start=Date.parse(b.openTimeUtc);return start<=at && at<start+b.intervalSeconds*1000;});
    if (!bar) return [];
    return [{ trade, side, at, time:Date.parse(bar.openTimeUtc)/1000 }];
  });
  return fills.sort((a,b)=>a.at-b.at || a.trade.signalId.localeCompare(b.trade.signalId));
}
