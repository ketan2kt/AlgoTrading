export interface DailyEntryEvidence {
  status: string; recordedAtUtc: string | null; extensionAtr: number | null;
  roomAtr: number | null; candleAgeSeconds: number | null; reasons: string[];
}
export interface DailyTradeReview {
  tradeId: string; entry: DailyEntryEvidence; grossGiveback: number | null;
  retainedPercent: number | null; profitEvidence: string; exitAssessment: string;
  finalExitUnits: number | null;
  followUps: {horizonMinutes: number; observedAtUtc: string; price: number; grossDifference: number}[];
}
export interface DailyMarketReview {
  market: string; closedTrades: number; openTrades: number; netPnl: number; costs: number;
  comparablePeakTrades: number; observedPeakSubtotal: number | null;
  grossGivebackSubtotal: number | null; positivePeakToNetLossTrades: number | null;
}
export interface DailyProfitReview { markets: DailyMarketReview[]; trades: DailyTradeReview[]; }
