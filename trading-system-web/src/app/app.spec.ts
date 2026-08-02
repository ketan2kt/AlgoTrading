import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { NEVER, Subject } from 'rxjs';
import { App } from './app';
import { AuthService, CurrentUser } from './auth.service';
import { GrowwTokenService } from './groww-token.service';
import { TradingWorkspaceService } from './trading-workspace.service';
import { PaperRiskService } from './paper-risk.service';

describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
  });

  it('renders paper mode and requires sign-in for the live workspace', async () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();

    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/system/status').flush({
      mode: 'Paper',
      liveTradingAvailable: false,
      tradingEnabled: false,
      status: 'FoundationOnly',
      observedAtUtc: '2026-07-30T04:00:00Z',
    });
    http.expectOne('/api/auth/me').flush(null, { status: 401, statusText: 'Unauthorized' });
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Paper');
    expect(fixture.nativeElement.textContent).toContain('Administrator sign in');
    expect(fixture.nativeElement.textContent).toContain('Live market visibility');
    expect(fixture.nativeElement.textContent).not.toContain('Operating mode');
    expect(fixture.nativeElement.textContent).not.toContain('Nifty command view');
    http.verify();
  });

  it('repaints immediately after authentication and always exposes token entry', async () => {
    const currentUser = new Subject<CurrentUser | null>();
    TestBed.overrideProvider(AuthService, {
      useValue: { currentUser: () => currentUser.asObservable() },
    });
    TestBed.overrideProvider(GrowwTokenService, {
      useValue: { getStatus: () => NEVER },
    });
    TestBed.overrideProvider(TradingWorkspaceService, {
      useValue: {
        getNifty: () => NEVER,
        updates$: () => NEVER,
        connect: () => Promise.resolve(),
        disconnect: () => Promise.resolve(),
      },
    });
    TestBed.overrideProvider(PaperRiskService, {
      useValue: { getKillSwitch: () => NEVER, setKillSwitch: () => NEVER },
    });

    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/system/status').flush({
      mode: 'Paper',
      liveTradingAvailable: false,
      tradingEnabled: false,
      status: 'FoundationOnly',
      observedAtUtc: '2026-08-03T03:00:00Z',
    });

    currentUser.next({ username: 'administrator', roles: ['Administrator'] });
    await fixture.whenStable();

    expect(fixture.nativeElement.textContent).toContain('Nifty command view');
    expect(fixture.nativeElement.textContent).toContain('Checking token status');
    expect(fixture.nativeElement.textContent).toContain('Add today’s token');
    http.verify();
  });
});
