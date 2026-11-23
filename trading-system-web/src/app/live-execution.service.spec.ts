import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { LiveExecutionService } from './live-execution.service';

describe('LiveExecutionService', () => {
  beforeEach(() => TestBed.configureTestingModule({
    providers: [provideHttpClient(), provideHttpClientTesting()],
  }));

  it('saves broker selection separately from arming with antiforgery protection',()=>{
    const service=TestBed.inject(LiveExecutionService), http=TestBed.inject(HttpTestingController);
    service.selectBroker('NIFTY','Upstox').subscribe();
    http.expectOne('/api/security/antiforgery-token').flush({token:'csrf'});
    const request=http.expectOne('/api/live-execution/markets/NIFTY/broker');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({broker:'Upstox'});
    expect(request.request.headers.get('X-CSRF-TOKEN')).toBe('csrf');
    request.flush({}); http.verify();
  });

  it('requires antiforgery protection when switching on one live market', () => {
    const service = TestBed.inject(LiveExecutionService);
    const http = TestBed.inject(HttpTestingController);
    service.setArmed('BANKEX', true).subscribe();

    http.expectOne('/api/security/antiforgery-token').flush({ token: 'csrf' });
    const request = http.expectOne('/api/live-execution/markets/BANKEX/arm');
    expect(request.request.method).toBe('PUT');
    expect(request.request.headers.get('X-CSRF-TOKEN')).toBe('csrf');
    expect(request.request.body).toEqual({ armed: true });
    request.flush({ buildEnabled: true, markets: [{ market: 'BANKEX', armed: true,
      changedAtUtc: null, changedBy: null }],
      mirrorsPaperQuantity: true, controlledBrokerTestCompleted: false,
      allowedMarkets: ['NIFTY', 'SENSEX'], changedAtUtc: null, changedBy: null });
    http.verify();
  });
});
