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

export interface WorkspaceVolumeBar {
  openTimeUtc: string;
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
  executionInstrument: string | null;
  executionInstrumentType: string | null;
  executionExpiry: string | null;
  executionStrike: number | null;
  executionLotSize: number | null;
  executionMaximumLots: number | null;
  executionProposedEntry: number | null;
  executionOneLotRisk: number | null;
  executionStopLoss: number | null;
  executionTarget: number | null;
  executionRiskAmount: number | null;
  executionCapitalExposure: number | null;
  rejectionReasons: string[];
  lifecycleStatus: string;
  currentOptionPrice: number | null;
  exitPrice: number | null;
  realisedPnl: number | null;
  unrealisedPnl: number | null;
  entryTimeUtc: string | null;
  exitTimeUtc: string | null;
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
  evaluations: WorkspaceStrategyEvaluation[];
  paperAutomation: PaperAutomationSnapshot;
  futuresVolume?: WorkspaceVolumeBar[] | null;
}

export function mergeWorkspaceSnapshot(
  current: TradingWorkspaceSnapshot | null,
  incoming: TradingWorkspaceSnapshot,
): TradingWorkspaceSnapshot {
  if (!current || current.exchange !== incoming.exchange || current.instrument !== incoming.instrument) {
    return incoming;
  }

  const currentLatest = latestCandleTimestamp(current);
  const incomingLatest = latestCandleTimestamp(incoming);
  const incomingIsOlder = incomingLatest < currentLatest ||
    (incomingLatest === currentLatest && incoming.candles.length < current.candles.length);

  return incomingIsOlder
    ? { ...incoming, candles: current.candles, futuresVolume: current.futuresVolume }
    : incoming;
}

function latestCandleTimestamp(snapshot: TradingWorkspaceSnapshot): number {
  const timestamp = snapshot.candles.at(-1)?.openTimeUtc;
  return timestamp ? new Date(timestamp).getTime() : Number.NEGATIVE_INFINITY;
}

export interface WorkspaceStrategyEvaluation {
  evaluationId: string;
  candleTimeUtc: string;
  strategy: string;
  outcome: string;
  currentPrice: number;
  openingRangeHigh: number;
  openingRangeLow: number;
  vwap: number;
  fastEma: number;
  slowEma: number;
  atrPercent: number;
  relativeFuturesVolume: number;
  regime: string;
  regimeBias: string | null;
  regimeConfidence: number;
  failedConditions: string[];
  signalId: string | null;
  optionSymbol: string | null;
  optionType: string | null;
  optionExpiry: string | null;
  optionStrike: number | null;
  optionPremium: number | null;
  realisedPnl: number | null;
  shadowStructureState: string | null;
  shadowTrendQuality: number | null;
  shadowWouldPermit: boolean | null;
  shadowEvidence: string[];
}

export interface PaperAutomationSnapshot {
  status: string;
  tradingPermitted: boolean;
  message: string;
  observedAtUtc: string;
  tradesToday: number;
  realisedPnl: number;
  unrealisedPnl: number;
  activeSignalId: string | null;
  activeDirection: string | null;
  activeQuantity: number | null;
  entryPrice: number | null;
  stopLoss: number | null;
  target: number | null;
  selectedOptionSymbol: string | null;
  selectedOptionType: string | null;
  selectedOptionExpiry: string | null;
  selectedOptionStrike: number | null;
  selectedOptionLotSize: number | null;
  readinessChecks: PaperReadinessCheck[] | null;
  currentOptionPrice: number | null;
  activePositionMarks?: PaperPositionMark[] | null;
  portfolioRisk?: PaperPortfolioRiskSnapshot | null;
}

export interface PaperPositionMark {
  signalId: string;
  currentPrice: number | null;
  executablePrice: number | null;
  unrealisedPnl: number | null;
  observedAtUtc: string;
  quoteAvailable: boolean;
}

export interface PaperPortfolioRiskSnapshot {
  openPositions: number;
  capitalExposure: number;
  openRiskAtStops: number;
  dailyLossConsumed: number;
  maximumDailyLoss: number;
  quoteUnavailablePositions: number;
  reconciliationHealthy: boolean;
  observedAtUtc: string;
}

export interface PaperReadinessCheck {
  code: string;
  label: string;
  ready: boolean;
  detail: string;
}
