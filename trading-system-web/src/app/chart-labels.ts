import { TradingWorkspaceSnapshot } from './trading-workspace';

export interface ChartPriceLineTitles {
  entry: string;
  stop: string;
  target: string;
}

export function chartPriceLineTitles(snapshot: TradingWorkspaceSnapshot): ChartPriceLineTitles {
  if (snapshot.exchange === 'MCX') {
    return { entry: 'FUTURES ENTRY', stop: 'STRUCTURAL SL', target: 'OBJECTIVE' };
  }
  const market = snapshot.exchange === 'BSE' ? 'SENSEX' : 'NIFTY';
  return {
    entry: `${market} TRIGGER`,
    stop: `${market} INVALIDATION`,
    target: `${market} OBJECTIVE`,
  };
}
