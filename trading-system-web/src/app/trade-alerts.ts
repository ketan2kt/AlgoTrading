import { TradingWorkspaceSnapshot } from './trading-workspace';

export type TradingMarketCode = 'nifty' | 'sensex' | 'natural-gas';
export type TradeAlertKind = 'entry' | 'exit';

export function marketCodeForSnapshot(snapshot: Pick<TradingWorkspaceSnapshot, 'exchange'>): TradingMarketCode {
  return snapshot.exchange === 'BSE' ? 'sensex' : snapshot.exchange === 'MCX' ? 'natural-gas' : 'nifty';
}

export function tradeAlertTransition(previous: string | undefined, current: string): TradeAlertKind | null {
  if (!previous) return 'entry';
  if (previous === current) return null;
  return ['SL hit', 'Target hit', 'Time exit', 'Emergency exit', 'Trend reversal exit', 'Closed'].includes(current)
    ? 'exit' : null;
}
