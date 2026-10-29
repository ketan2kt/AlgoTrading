import { HttpClient, HttpHeaders } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable, switchMap } from 'rxjs';

export interface LiveExecutionStatus {
  buildEnabled: boolean;
  markets: { market: string; armed: boolean; changedAtUtc: string | null; changedBy: string | null }[];
  mirrorsPaperQuantity: boolean;
  controlledBrokerTestCompleted: boolean;
  allowedMarkets: string[];
  latestIntentMarket: string | null;
  latestIntentStatus: string | null;
  latestIntentError: string | null;
  latestIntentAtUtc: string | null;
}

@Injectable({ providedIn: 'root' })
export class LiveExecutionService {
  private readonly http = inject(HttpClient);

  getStatus(): Observable<LiveExecutionStatus> {
    return this.http.get<LiveExecutionStatus>('/api/live-execution/status');
  }

  setArmed(market: string, armed: boolean): Observable<LiveExecutionStatus> {
    return this.http.get<{ token: string }>('/api/security/antiforgery-token').pipe(
      switchMap(({ token }) => this.http.put<LiveExecutionStatus>(`/api/live-execution/markets/${market}/arm`,
        { armed }, { headers: new HttpHeaders({ 'X-CSRF-TOKEN': token }) })),
    );
  }
}
