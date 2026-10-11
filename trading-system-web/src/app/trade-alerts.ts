import { TradingWorkspaceSnapshot } from './trading-workspace';

export type TradingMarketCode = 'nifty' | 'sensex' | 'nifty-scalp' | 'sensex-scalp' | 'natural-gas';
export type TradeAlertKind = 'entry' | 'exit';

export function marketCodeForSnapshot(snapshot: Pick<TradingWorkspaceSnapshot, 'exchange' | 'marketCode'>): TradingMarketCode {
  if (snapshot.marketCode === 'nifty-scalp' || snapshot.marketCode === 'sensex-scalp') return snapshot.marketCode;
  return snapshot.exchange === 'BSE' ? 'sensex' : snapshot.exchange === 'MCX' ? 'natural-gas' : 'nifty';
}

export function tradeAlertTransition(previous: string | undefined, current: string): TradeAlertKind | null {
  if (!previous) return 'entry';
  if (previous === current) return null;
  return ['SL hit', 'Target hit', 'Time exit', 'Emergency exit', 'Trend reversal exit', 'Closed',
    'StopLossHit', 'TargetHit', 'FastFailureExit', 'StaleExit', 'TimeExit'].includes(current)
    ? 'exit' : null;
}
