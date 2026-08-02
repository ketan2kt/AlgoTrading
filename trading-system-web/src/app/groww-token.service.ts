import { HttpClient, HttpHeaders } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable, switchMap } from 'rxjs';

export interface GrowwTokenStatus {
  isConfigured: boolean;
  isExpired: boolean;
  expiresAtUtc: string | null;
  updatedAtUtc: string | null;
  source: string;
}

@Injectable({ providedIn: 'root' })
export class GrowwTokenService {
  private readonly http = inject(HttpClient);

  getStatus(): Observable<GrowwTokenStatus> {
    return this.http.get<GrowwTokenStatus>('/api/broker/groww/access-token/status');
  }

  store(accessToken: string): Observable<GrowwTokenStatus> {
    return this.http
      .get<{ token: string }>('/api/security/antiforgery-token')
      .pipe(
        switchMap(({ token }) =>
          this.http.post<GrowwTokenStatus>(
            '/api/broker/groww/access-token',
            { accessToken },
            { headers: new HttpHeaders({ 'X-CSRF-TOKEN': token }) },
          ),
        ),
      );
  }
}
