import { HttpClient, HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { catchError, map, Observable, of, switchMap } from 'rxjs';

export interface CurrentUser {
  username: string;
  roles: string[];
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);

  currentUser(): Observable<CurrentUser | null> {
    return this.http.get<CurrentUser>('/api/auth/me').pipe(
      catchError((error: HttpErrorResponse) => {
        if (error.status === 401) return of(null);
        throw error;
      }),
    );
  }

  login(username: string, password: string): Observable<CurrentUser> {
    return this.http.get<{ token: string }>('/api/security/antiforgery-token').pipe(
      switchMap(({ token }) =>
        this.http.post<void>(
          '/api/auth/login',
          { username, password },
          {
            headers: new HttpHeaders({ 'X-CSRF-TOKEN': token }),
          },
        ),
      ),
      switchMap(() => this.http.get<CurrentUser>('/api/auth/me')),
    );
  }
}
