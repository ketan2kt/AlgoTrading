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
  research?: DailyResearchPipelineReport | null;
}

export interface DailyResearchPipelineReport {
  version: string;
  market: string;
  sessionDate: string;
  decisionSnapshots: number;
  actionableCandidates: number;
  counterfactualWins: number;
  counterfactualLosses: number;
  unresolved: number;
  marketStates: Record<string, number>;
  walkForward: StrategyValidation[];
  promotion: { eligible: boolean; status: string; reasons: string[] };
  generatedAtUtc: string;
  intelligence?: ResearchIntelligence | null;
}

export interface ResearchIntelligence {
  version: string;
  entryTiming: {
    candidates: number; matureMoveEntries: number; earlierOneBarWasBetter: number;
    earlierTwoBarsWasBetter: number; averageMoveMaturityAtr: number;
    averageMaximumFavourableExcursionR: number; averageMaximumAdverseExcursionR: number;
    averageDirectionalReturnByHorizon: Record<string, number>;
  };
  missedMoves: { noCandidateSnapshots: number; materialMissedMoves: number; leadingBlockers: Record<string, number> };
  regimeConfusion: { assessed: number; correct: number; incorrect: number; accuracy: number; confusionPairs: Record<string, number> };
  cohorts: ResearchCohort[];
  exits: { assessed: number; targetFirst: number; stopFirst: number; extendedRunnerCandidates: number; tightStopCandidates: number; averageMfeR: number; averageMaeR: number };
  execution: { chargesAppliedToExecutedTrades: boolean; bidAskAndSlippageAvailable: boolean; partialFillSimulationAvailable: boolean; status: string };
  dataQuality: { candles: number; expectedIntervalSeconds: number; missingIntervals: number; duplicateTimestamps: number; nonPositivePrices: number; completeness: number; status: string };
  recommendations: { code: string; priority: string; finding: string; proposedExperiment: string; supportingObservations: number }[];
  guardrails: string[];
  advanced?: AdvancedMarketIntelligence | null;
}

export interface AdvancedMarketIntelligence {
  version: string;
  volatility: { state: string; currentAtrPercent: number; baselineAtrPercent: number; percentile: number; expansionRatio: number };
  options: { available: boolean; contracts: number; putCallOiRatio: number; averageImpliedVolatility: number; ivSkew: number; liquidContracts: number; spreadStatus: string; evidence: string };
  crossMarket: { available: boolean; returnCorrelation: number; alignment: string; evidence: string };
  capabilities: { capability: string; status: string; evidence: string }[];
  levels: { kind: string; price: number; strength: number; evidence: string }[];
  volumeProfile: { available: boolean; pointOfControl: number | null; valueAreaLow: number | null; valueAreaHigh: number | null; anchoredVwap: number | null; evidence: string };
  setupLifecycle: { phase: string; direction: string | null; extensionAtr: number; ageCandles: number; evidence: string };
  metaLabel: { acceptanceProbability: number; verdict: string; evidence: string[] };
  uncertainty: { score: number; verdict: string; reasons: string[] };
  portfolio: { verdict: string; correlation: number; evidence: string };
  adaptiveRisk: { researchSizeMultiplier: number; verdict: string; evidence: string[] };
  drift: { status: string; volatilityRatio: number; regimeDistributionShift: number; evidence: string[] };
  experiments: { id: string; hypothesis: string; status: string; minimumCandidates: number; minimumSessions: number; successCriteria: string; rollbackCriteria: string }[];
}

export interface ResearchCohort {
  strategy: string; marketState: string; direction: string; timeBucket: string;
  candidates: number; wins: number; winRate: number; averageMfeR: number; averageMaeR: number;
}

export interface StrategyValidation {
  strategy: string;
  trainingCandidates: number;
  trainingWinRate: number;
  validationCandidates: number;
  validationWinRate: number;
  status: string;
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
