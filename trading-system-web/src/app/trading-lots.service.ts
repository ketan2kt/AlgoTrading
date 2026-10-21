import { HttpClient, HttpHeaders } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable, switchMap } from 'rxjs';

export interface TradingLotConfiguration {
  niftyLots: number;
  sensexLots: number;
  updatedAtUtc: string | null;
}

@Injectable({ providedIn: 'root' })
export class TradingLotsService {
  private readonly http = inject(HttpClient);

  get(): Observable<TradingLotConfiguration> {
    return this.http.get<TradingLotConfiguration>('/api/trading-lots');
  }

  save(niftyLots: number, sensexLots: number): Observable<TradingLotConfiguration> {
    return this.http.get<{ token: string }>('/api/security/antiforgery-token').pipe(
      switchMap(({ token }) => this.http.put<TradingLotConfiguration>('/api/trading-lots',
        { niftyLots, sensexLots }, { headers: new HttpHeaders({ 'X-CSRF-TOKEN': token }) })),
    );
  }
}
