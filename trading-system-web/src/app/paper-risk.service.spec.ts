import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { PaperRiskService } from './paper-risk.service';

describe('PaperRiskService', () => {
  beforeEach(() => TestBed.configureTestingModule({
    providers: [provideHttpClient(), provideHttpClientTesting()],
  }));

  it('changes the kill switch only through an antiforgery-protected request', () => {
    const service = TestBed.inject(PaperRiskService);
    const http = TestBed.inject(HttpTestingController);
    service.setKillSwitch(true, 'Emergency stop test.').subscribe();

    http.expectOne('/api/security/antiforgery-token').flush({ token: 'csrf' });
    const request = http.expectOne('/api/paper/risk/kill-switch');
    expect(request.request.method).toBe('PUT');
    expect(request.request.headers.get('X-CSRF-TOKEN')).toBe('csrf');
    expect(request.request.body).toEqual({ active: true, reason: 'Emergency stop test.' });
    request.flush({ active: true, updatedAtUtc: '2026-08-03T04:00:00Z' });
    http.verify();
  });
});
