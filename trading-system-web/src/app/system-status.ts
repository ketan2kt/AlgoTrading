export interface SystemStatus {
  mode: 'Backtest' | 'Paper' | 'Live';
  liveTradingAvailable: boolean;
  tradingEnabled: boolean;
  status: string;
  observedAtUtc: string;
}
