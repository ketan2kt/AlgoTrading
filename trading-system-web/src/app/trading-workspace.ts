export interface WorkspaceCandle {
  openTimeUtc: string;
  intervalSeconds: number;
  open: number;
  high: number;
  low: number;
  close: number;
  volume: number;
  isClosed: boolean;
}

export interface WorkspaceTradeOverlay {
  signalId: string;
  strategy: string;
  direction: string;
  signalTimeUtc: string;
  entry: number;
  stopLoss: number;
  target: number;
  status: string;
  quantity: number | null;
  fillPrice: number | null;
}

export interface TradingWorkspaceSnapshot {
  instrument: string;
  exchange: string;
  timeframe: string;
  mode: string;
  feedStatus: string;
  isLive: boolean;
  isFresh: boolean;
  lastMarketTimestampUtc: string | null;
  observedAtUtc: string;
  statusMessage: string | null;
  candles: WorkspaceCandle[];
  overlays: WorkspaceTradeOverlay[];
}
