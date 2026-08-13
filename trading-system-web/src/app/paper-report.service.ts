import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';

export interface PaperTradeHistoryItem { signalId:string; contract:string; direction:string; quantity:number; entryPrice:number; exitPrice:number; realisedPnl:number; exitReason:string; signalTimeUtc:string; exitTimeUtc:string; strategy:string; regime:string; daysToExpiry:number; }
export interface DailyPaperPerformance { date:string; trades:number; wins:number; losses:number; netPnl:number; grossProfit:number; grossLoss:number; winRate:number; maximumDrawdown:number; }
export interface StrategyPerformanceBreakdown { strategy:string; regime:string; timeBucket:string; daysToExpiry:number; trades:number; wins:number; netPnl:number; winRate:number; averagePnl:number; }
export interface PaperTradingReport { daily:DailyPaperPerformance[]; trades:PaperTradeHistoryItem[]; breakdown:StrategyPerformanceBreakdown[]; observedAtUtc:string; }
@Injectable({providedIn:'root'})
export class PaperReportService {
  private readonly http=inject(HttpClient);
  get(){return this.http.get<PaperTradingReport>('/api/reports/paper-trading?days=30');}
}
