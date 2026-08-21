import { TradingWorkspaceSnapshot } from './trading-workspace';

export interface ChartPriceLineTitles {
  entry: string;
  stop: string;
  target: string;
}

export function chartPriceLineTitles(
  snapshot: TradingWorkspaceSnapshot,
  direction?: string,
): ChartPriceLineTitles {
  if (snapshot.exchange === 'MCX') {
    const side = direction === 'Sell' ? 'SELL' : direction === 'Buy' ? 'BUY' : 'FUTURES';
    return { entry: `${side} ENTRY`, stop: 'STRUCTURAL SL', target: 'OBJECTIVE' };
  }
  const market = snapshot.exchange === 'BSE' ? 'SENSEX' : 'NIFTY';
  return {
    entry: `${market} TRIGGER`,
    stop: `${market} INVALIDATION`,
    target: `${market} OBJECTIVE`,
  };
}
