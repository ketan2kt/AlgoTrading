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
  const market = snapshot.marketCode === 'bank-nifty' ? 'BANK NIFTY' :
    snapshot.marketCode === 'bankex' ? 'BANKEX' :
    snapshot.marketCode === 'sensex' ? 'SENSEX' :
    snapshot.marketCode === 'nifty' ? 'NIFTY' :
    snapshot.exchange === 'BSE' ? 'SENSEX' : 'NIFTY';
  return {
    entry: `${market} TRIGGER`,
    stop: `${market} INVALIDATION`,
    target: `${market} OBJECTIVE`,
  };
}
