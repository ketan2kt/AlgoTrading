import { DatePipe, DecimalPipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { Component, EventEmitter, HostListener, inject, OnInit, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';

export interface PreSessionEvidence {
  name: string;
  status: string;
  value: string | null;
  source: string | null;
  sourceAtUtc: string | null;
  note: string;
}

export interface PreSessionMarketBrief {
  market: string;
  sessionDate: string;
  stage: string;
  capturedAtUtc: string;
  bias: string;
  headline: string;
  judgement: string;
  confidence: string;
  judgementReason: string;
  previousOpen: number | null;
  previousClose: number | null;
  previousHigh: number | null;
  previousLow: number | null;
  previousSessionAtUtc: string | null;
  indicativePrice: number | null;
  indicativePriceAtUtc: string | null;
  callOiStrike: number | null;
  putOiStrike: number | null;
  optionExpiry: string | null;
  optionOiAtUtc: string | null;
  scenarios: string[];
  evidence: PreSessionEvidence[];
  limitations: string[];
}

export interface PreSessionBriefReport {
  sessionDate: string;
  generatedAtUtc: string;
  briefs: PreSessionMarketBrief[];
  missingStages: string[];
  outcomes: PreSessionObservedOutcome[];
  scorecards: PreSessionScorecard[];
}

export interface PreSessionScorecard {
  market: string;
  reviewedSessions: number;
  directionalGuesses: number;
  correct: number;
  incorrect: number;
  accuracyPercent: number | null;
}

export interface PreSessionObservedOutcome {
  market: string;
  sessionDate: string;
  open: number;
  high: number;
  low: number;
  close: number;
  recordedCandles: number;
  lastCandleAtUtc: string;
  crossedPriorHigh: boolean | null;
  crossedPriorLow: boolean | null;
  actualDirection: string;
  morningJudgement: string;
  assessment: string;
  coverage: string;
  assessedAtUtc: string;
}

@Component({
  selector: 'app-pre-session-brief',
  imports: [DatePipe, DecimalPipe, FormsModule],
  templateUrl: './pre-session-brief.component.html',
  styleUrl: './pre-session-brief.component.scss',
})
export class PreSessionBriefComponent implements OnInit {
  private readonly http = inject(HttpClient);
  @Output() readonly closed = new EventEmitter<void>();

  protected sessionDate = new Date().toLocaleDateString('en-CA', { timeZone: 'Asia/Kolkata' });
  protected report: PreSessionBriefReport | null = null;
  protected loading = false;
  protected error = '';

  ngOnInit(): void { this.load(); }

  @HostListener('document:keydown.escape')
  protected closeOnEscape(): void { this.closed.emit(); }

  protected load(): void {
    this.loading = true;
    this.error = '';
    this.report = null;
    this.http.get<PreSessionBriefReport>('/api/market-analysis/pre-session',
      { params: { date: this.sessionDate } }).subscribe({
      next: report => { this.report = report; this.loading = false; },
      error: () => { this.error = 'Pre-session evidence is unavailable. Try refreshing the report.'; this.loading = false; },
    });
  }

  protected latest(market: string): PreSessionMarketBrief | null {
    const briefs = this.report?.briefs.filter(brief => brief.market === market) ?? [];
    return briefs.find(brief => brief.stage === 'PreOpen') ??
      briefs.find(brief => brief.stage === 'Overnight') ?? null;
  }

  protected outcome(market: string): PreSessionObservedOutcome | null {
    return this.report?.outcomes.find(value => value.market === market) ?? null;
  }

  protected scorecard(market: string): PreSessionScorecard | null {
    return this.report?.scorecards.find(value => value.market === market) ?? null;
  }
}
