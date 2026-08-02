import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { App } from './app';

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
});
