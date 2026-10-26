import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';

export interface TradeExitQualityMetrics { maximumFavourableExcursion:number; maximumAdverseExcursion:number; profitGiveback:number; capturedProfitRatio:number; bestPostExitIncrementalPnl:number; priceSamples:number; assessment:string; maximumFavourableAtUtc:string|null; maximumAdverseAtUtc:string|null; }
export interface PaperTradingCostBreakdown { scheduleVersion:string; brokerage:number; securitiesTransactionTax:number; exchangeTransactionCharges:number; investorProtectionFund:number; sebiTurnoverFees:number; goodsAndServicesTax:number; stampDuty:number; total:number; }
export interface PaperTradeHistoryItem { signalId:string; contract:string; direction:string; quantity:number; entryPrice:number; exitPrice:number; realisedPnl:number; exitReason:string; signalTimeUtc:string; exitTimeUtc:string; strategy:string; regime:string; daysToExpiry:number; costs:PaperTradingCostBreakdown; exitQuality:TradeExitQualityMetrics; shadowStructureState:string|null; shadowTrendQuality:number|null; shadowWouldPermit:boolean|null; }
export interface DailyPaperPerformance { date:string; trades:number; wins:number; losses:number; netPnl:number; grossProfit:number; grossLoss:number; winRate:number; maximumDrawdown:number; }
export interface StrategyPerformanceBreakdown { strategy:string; regime:string; timeBucket:string; daysToExpiry:number; trades:number; wins:number; netPnl:number; winRate:number; averagePnl:number; expectancy:number; profitFactor:number; }
export interface DecisionReasonCount { reason:string; count:number; }
export interface StrategyDecisionFunnel { outcome:string; evaluations:number; averageConfidence:number; averageRelativeFuturesVolume:number; leadingReasons:DecisionReasonCount[]; }
export interface ResearchRecommendation { code:string; severity:string; message:string; supportingTrades:number; eligibleForExperiment:boolean; }
export interface PaperResearchSummary { closedTrades:number; expectancy:number; profitFactor:number; averageMaximumFavourableExcursion:number; averageMaximumAdverseExcursion:number; averageProfitGiveback:number; earlyExitCandidates:number; profitGivebackCandidates:number; }
export interface ShadowStructurePerformance { state:string; wouldPermit:boolean; trades:number; wins:number; netPnl:number; expectancy:number; }
export interface ReplayMetrics { trades:number; wins:number; netPnl:number; winRate:number; expectancy:number; profitFactor:number; maximumDrawdown:number; }
export interface ReplayRobustness { validationDays:number; positiveValidationDays:number; positiveDayRate:number; verdict:'Candidate'|'Rejected'|'InsufficientEvidence'; reason:string; }
export interface ReplayVariantResult { code:string; description:string; training:ReplayMetrics; validation:ReplayMetrics; coveredTrades:number; robustness:ReplayRobustness; }
export interface PaperStrategyReplayReport { sourceTrades:number; tradesWithPricePath:number; rejectedEvaluationsWithoutOptionPath:number; trainingFraction:number; variants:ReplayVariantResult[]; limitations:string[]; }
export interface PaperTradingReport { daily:DailyPaperPerformance[]; trades:PaperTradeHistoryItem[]; breakdown:StrategyPerformanceBreakdown[]; decisionFunnel:StrategyDecisionFunnel[]; recommendations:ResearchRecommendation[]; research:PaperResearchSummary; shadowStructure:ShadowStructurePerformance[]; replay:PaperStrategyReplayReport; observedAtUtc:string; }
export interface PaperMarketPnl { market:string; trades:number; wins:number; losses:number; grossProfit:number; grossLoss:number; charges:number; netPnl:number; }
export interface PaperPnlSummary { from:string; to:string; market:string; markets:PaperMarketPnl[]; totalTrades:number; totalCharges:number; totalNetPnl:number; observedAtUtc:string; }
export type DailyDiagnosticsTrade={market:string;tradeId:string;symbol:string;strategy:string;direction:string;quantity:number;entryPrice:number;exitOrCurrentPrice:number;openedAtUtc:string;closedAtUtc:string|null;exitReason:string|null;grossPnl:number;costs:number;netRealisedPnl:number;maximumFavourableExcursion:number|null;maximumAdverseExcursion:number|null;signalId:string|null};
export type DailyDiagnostics={tradingDate:string;market:string;generatedAtUtc:string;summary:{decisionCount:number;acceptedDecisionCount:number;rejectedDecisionCount:number;completedTradeCount:number;openTradeCount:number;wins:number;losses:number;grossPnl:number;costs:number;netRealisedPnl:number;outcomes:Record<string,number>;lastCandleAtUtc:string|null};decisions:{market:string;source:string;candleTimeUtc:string;outcome:string;confidence:number;regime:string|null;bias:string|null;price:number|null;evidenceJson:string;signalId:string|null}[];trades:DailyDiagnosticsTrade[];systemEvents:{occurredAtUtc:string;severity:string;eventType:string;message:string;detailsJson:string|null}[];telemetryWarnings:string[];feeds:{provider:string;isAvailable:boolean;errorCode:string|null;lastSuccessAtUtc:string|null}[]};
@Injectable({providedIn:'root'})
export class PaperReportService {
  private readonly http=inject(HttpClient);
  get(){return this.http.get<PaperTradingReport>('/api/market-analysis?days=30');}
  getDaily(date:string){return this.http.get<DailyDiagnostics>('/api/market-analysis/daily',{params:{date}});}
  getPnlSummary(from:string,to:string,market:string){
    return this.http.get<PaperPnlSummary>('/api/market-analysis/pnl-summary',{params:{from,to,market}});
  }
}
