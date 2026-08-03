import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { GrowwTokenService } from './groww-token.service';

describe('GrowwTokenService', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [GrowwTokenService, provideHttpClient(), provideHttpClientTesting()],
    });
  });

  it('stores a token only through an antiforgery-protected request', () => {
    const service = TestBed.inject(GrowwTokenService);
    const http = TestBed.inject(HttpTestingController);
    const accessToken = 'secure-daily-groww-access-token-value';
    const expected = {
      token: {
        isConfigured: true,
        isExpired: false,
        expiresAtUtc: '2026-08-03T00:30:00Z',
        updatedAtUtc: '2026-08-02T18:30:00Z',
        source: 'ProtectedDatabase',
      },
      instrumentSynchronization: {
        downloaded: 100,
        inserted: 90,
        updated: 0,
        skipped: 10,
        completedAtUtc: '2026-08-02T18:30:01Z',
      },
      instrumentSynchronizationError: null,
    };

    service.store(accessToken).subscribe((status) => expect(status).toEqual(expected));
    http.expectOne('/api/security/antiforgery-token').flush({ token: 'csrf-token' });
    const request = http.expectOne('/api/broker/groww/access-token');
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('X-CSRF-TOKEN')).toBe('csrf-token');
    expect(request.request.body).toEqual({ accessToken });
    request.flush(expected);
    http.verify();
  });

  it('manually synchronises instruments through an antiforgery-protected request', () => {
    const service = TestBed.inject(GrowwTokenService);
    const http = TestBed.inject(HttpTestingController);
    const expected = {
      downloaded: 100,
      inserted: 0,
      updated: 90,
      skipped: 10,
      completedAtUtc: '2026-08-02T18:35:00Z',
    };

    service.synchronizeInstruments().subscribe((result) => expect(result).toEqual(expected));
    http.expectOne('/api/security/antiforgery-token').flush({ token: 'csrf-token' });
    const request = http.expectOne('/api/broker/groww/access-token/synchronize-instruments');
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('X-CSRF-TOKEN')).toBe('csrf-token');
    request.flush(expected);
    http.verify();
  });
});
