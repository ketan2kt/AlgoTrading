import { HttpClient, HttpHeaders } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable, switchMap } from 'rxjs';

export interface KillSwitchStatus {
  active: boolean;
  updatedAtUtc: string | null;
}

export interface DailyLossOverrideStatus {
  active: boolean;
  sessionDate: string;
  updatedAtUtc: string | null;
}

@Injectable({ providedIn: 'root' })
export class PaperRiskService {
  private readonly http = inject(HttpClient);

  getKillSwitch(): Observable<KillSwitchStatus> {
    return this.http.get<KillSwitchStatus>('/api/paper/risk/kill-switch');
  }

  setKillSwitch(active: boolean, reason: string): Observable<KillSwitchStatus> {
    return this.http.get<{ token: string }>('/api/security/antiforgery-token').pipe(
      switchMap(({ token }) => this.http.put<KillSwitchStatus>('/api/paper/risk/kill-switch',
        { active, reason }, { headers: new HttpHeaders({ 'X-CSRF-TOKEN': token }) })),
    );
  }

  getDailyLossOverride(): Observable<DailyLossOverrideStatus> {
    return this.http.get<DailyLossOverrideStatus>('/api/paper/risk/daily-loss-override');
  }

  setDailyLossOverride(active: boolean, reason: string): Observable<DailyLossOverrideStatus> {
    return this.http.get<{ token: string }>('/api/security/antiforgery-token').pipe(
      switchMap(({ token }) => this.http.put<DailyLossOverrideStatus>(
        '/api/paper/risk/daily-loss-override', { active, reason },
        { headers: new HttpHeaders({ 'X-CSRF-TOKEN': token }) })),
    );
  }
}
