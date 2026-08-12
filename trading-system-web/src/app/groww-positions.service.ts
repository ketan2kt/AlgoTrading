import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';

export interface GrowwLivePosition {
  tradingSymbol: string;
  segment: string;
  exchange: string;
  product: string;
  quantity: number;
  averagePrice: number;
  currentPrice: number | null;
  realisedPnl: number;
  unrealisedPnl: number | null;
}

export interface GrowwPositionsResponse {
  positions: GrowwLivePosition[];
  observedAtUtc: string;
  capability: 'ReadOnly';
}

@Injectable({ providedIn: 'root' })
export class GrowwPositionsService {
  private readonly http = inject(HttpClient);
  get() { return this.http.get<GrowwPositionsResponse>('/api/broker/groww/positions'); }
}
