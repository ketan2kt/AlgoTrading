import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { App } from './app';

describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideHttpClient(), provideHttpClientTesting()]
    }).compileComponents();
  });

  it('renders the server-authoritative paper mode and fail-closed state', async () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();

    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/system/status').flush({
      mode: 'Paper',
      liveTradingAvailable: false,
      tradingEnabled: false,
      status: 'FoundationOnly',
      observedAtUtc: '2026-07-30T04:00:00Z'
    });
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Paper');
    expect(fixture.nativeElement.textContent).toContain('Fail-closed');
    expect(fixture.nativeElement.textContent).toContain('Absent');
    http.verify();
  });
});
