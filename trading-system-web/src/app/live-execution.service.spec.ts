import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { LiveExecutionService } from './live-execution.service';

describe('LiveExecutionService', () => {
  beforeEach(() => TestBed.configureTestingModule({
    providers: [provideHttpClient(), provideHttpClientTesting()],
  }));

  it('requires antiforgery protection when arming live execution', () => {
    const service = TestBed.inject(LiveExecutionService);
    const http = TestBed.inject(HttpTestingController);
    service.setArmed(true, 'Controlled live activation.', 'admin-password').subscribe();

    http.expectOne('/api/security/antiforgery-token').flush({ token: 'csrf' });
    const request = http.expectOne('/api/live-execution/arm');
    expect(request.request.method).toBe('PUT');
    expect(request.request.headers.get('X-CSRF-TOKEN')).toBe('csrf');
    expect(request.request.body).toEqual({
      armed: true, reason: 'Controlled live activation.', password: 'admin-password',
    });
    request.flush({ buildEnabled: true, armed: true, armedForTradingDate: '2026-09-01',
      maximumLotsPerOrder: 5, controlledBrokerTestCompleted: false,
      allowedMarkets: ['NIFTY', 'SENSEX'], changedAtUtc: null, changedBy: null });
    http.verify();
  });
});
