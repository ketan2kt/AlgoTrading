import { HttpClient, HttpHeaders } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable, switchMap } from 'rxjs';

export interface LiveExecutionStatus {
  buildEnabled: boolean;
  markets: { market: string; armed: boolean; changedAtUtc: string | null; changedBy: string | null; executionBroker?: 'Groww' | 'Upstox'; entryStatus?: 'Unknown'|'Off'|'Blocked'|'Monitoring'; entryStatusReason?: string; entryCheckedAtUtc?: string|null }[];
  mirrorsPaperQuantity: boolean;
  controlledBrokerTestCompleted: boolean;
  allowedMarkets: string[];
  latestIntentMarket: string | null;
  latestIntentStatus: string | null;
  latestIntentError: string | null;
  latestIntentAtUtc: string | null;
}

export interface UpstoxExecutionReadiness {
  configured: boolean;
  authorized: boolean;
  staticIpVerified: boolean;
  sandbox: boolean;
  expiresAtUtc: string | null;
  exchanges: string[];
  message: string;
}

@Injectable({ providedIn: 'root' })
export class LiveExecutionService {
  private readonly http = inject(HttpClient);

  getStatus(): Observable<LiveExecutionStatus> {
    return this.http.get<LiveExecutionStatus>('/api/live-execution/status');
  }

  getUpstoxStatus(): Observable<UpstoxExecutionReadiness> {
    return this.http.get<UpstoxExecutionReadiness>('/api/upstox-execution/status');
  }

  connectUpstox(): Observable<{url: string}> {
    return this.http.get<{token: string}>('/api/security/antiforgery-token').pipe(
      switchMap(({token}) => this.http.post<{url: string}>('/api/upstox-execution/connect', {},
        {headers: new HttpHeaders({'X-CSRF-TOKEN': token})})),
    );
  }

  selectBroker(market: string, broker: 'Groww' | 'Upstox'): Observable<LiveExecutionStatus> {
    return this.http.get<{token: string}>('/api/security/antiforgery-token').pipe(
      switchMap(({token}) => this.http.put<LiveExecutionStatus>(`/api/live-execution/markets/${market}/broker`,
        {broker}, {headers: new HttpHeaders({'X-CSRF-TOKEN': token})})),
    );
  }

  setArmed(market: string, armed: boolean): Observable<LiveExecutionStatus> {
    return this.http.get<{ token: string }>('/api/security/antiforgery-token').pipe(
      switchMap(({ token }) => this.http.put<LiveExecutionStatus>(`/api/live-execution/markets/${market}/arm`,
        { armed }, { headers: new HttpHeaders({ 'X-CSRF-TOKEN': token }) })),
    );
  }
}
