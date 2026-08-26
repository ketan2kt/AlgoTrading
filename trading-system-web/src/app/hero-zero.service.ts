import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';

export interface HeroZeroCandidate {
  symbol: string; optionType: string; strike: number; expiry: string; lotSize: number;
  premium: number; bid: number; ask: number; spreadPercent: number; volume: number;
  openInterest: number; openInterestChange: number; score: number;
}
export interface HeroZeroLeg {
  positionId: string; symbol: string; optionType: string; strike: number; quantity: number;
  entryPremium: number; currentPremium: number; stopPremium: number; status: string;
  unrealisedPnl: number; realisedPnl: number | null;
}
export interface HeroZeroMonitor {
  market: string; isExpirySession: boolean; expiry: string | null; status: string;
  explanation: string; observedAtUtc: string; spotPrice: number | null;
  callCandidate: HeroZeroCandidate | null; putCandidate: HeroZeroCandidate | null;
  activeLegs: HeroZeroLeg[]; combinedEntryCost: number; combinedCurrentValue: number;
  combinedPnl: number;
}

@Injectable({ providedIn: 'root' })
export class HeroZeroService {
  private readonly http = inject(HttpClient);
  get(market: 'nifty' | 'sensex') {
    return this.http.get<HeroZeroMonitor>(`/api/hero-zero/${market}`);
  }
}
