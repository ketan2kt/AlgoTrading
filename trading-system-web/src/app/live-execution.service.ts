import { HttpClient, HttpHeaders } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable, switchMap } from 'rxjs';

export interface LiveExecutionStatus {
  buildEnabled: boolean;
  armed: boolean;
  armedForTradingDate: string | null;
  maximumLotsPerOrder: number;
  controlledBrokerTestCompleted: boolean;
  allowedMarkets: string[];
  changedAtUtc: string | null;
  changedBy: string | null;
}

@Injectable({ providedIn: 'root' })
export class LiveExecutionService {
  private readonly http = inject(HttpClient);

  getStatus(): Observable<LiveExecutionStatus> {
    return this.http.get<LiveExecutionStatus>('/api/live-execution/status');
  }

  setArmed(armed: boolean, reason: string, password: string): Observable<LiveExecutionStatus> {
    return this.http.get<{ token: string }>('/api/security/antiforgery-token').pipe(
      switchMap(({ token }) => this.http.put<LiveExecutionStatus>('/api/live-execution/arm',
        { armed, reason, password }, { headers: new HttpHeaders({ 'X-CSRF-TOKEN': token }) })),
    );
  }
}
