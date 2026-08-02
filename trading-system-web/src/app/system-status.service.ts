import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { SystemStatus } from './system-status';

@Injectable({ providedIn: 'root' })
export class SystemStatusService {
  private readonly http = inject(HttpClient);

  getCurrent(): Observable<SystemStatus> {
    return this.http.get<SystemStatus>('/api/system/status');
  }
}
