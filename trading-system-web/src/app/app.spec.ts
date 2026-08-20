import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Component, Input } from '@angular/core';
import { NEVER, Subject, of } from 'rxjs';
import { App } from './app';
import { AuthService, CurrentUser } from './auth.service';
import { GrowwTokenService } from './groww-token.service';
import { TradingWorkspaceService } from './trading-workspace.service';
import { PaperRiskService } from './paper-risk.service';
import { NiftyChartComponent } from './nifty-chart.component';
import { TradingWorkspaceSnapshot } from './trading-workspace';

@Component({ selector: 'app-nifty-chart', standalone: true, template: '' })
class MockNiftyChartComponent {
  @Input({ required: true }) snapshot!: TradingWorkspaceSnapshot;
  @Input() timeframeMinutes = 5;
}

describe('App', () => {
  beforeEach(async () => {
    window.history.replaceState({}, '', '/');
    TestBed.configureTestingModule({
      imports: [App],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    TestBed.overrideComponent(App, {
      remove: { imports: [NiftyChartComponent] },
      add: { imports: [MockNiftyChartComponent] },
    });
    await TestBed.compileComponents();
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
        getMarket: () => NEVER,
        updates$: () => NEVER,
        connect: () => Promise.resolve(),
        disconnect: () => Promise.resolve(),
      },
    });
    TestBed.overrideProvider(PaperRiskService, {
      useValue: {
        getKillSwitch: () => NEVER, setKillSwitch: () => NEVER,
        getDailyLossOverride: () => NEVER, setDailyLossOverride: () => NEVER,
      },
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

    expect(fixture.nativeElement.textContent).toContain('Markets');
    expect(fixture.nativeElement.textContent).toContain('Sensex');
    const niftyButton = Array.from(fixture.nativeElement.querySelectorAll('button'))
      .find((button: unknown) => (button as HTMLButtonElement).textContent?.includes('Nifty')) as HTMLButtonElement;
    niftyButton.click();
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Nifty command view');
    expect(fixture.nativeElement.textContent).toContain('Checking token status');
    expect(fixture.nativeElement.textContent).not.toContain('Enable loud trade alerts');
    expect(fixture.nativeElement.textContent).not.toContain('Trade alerts on');
    expect(fixture.nativeElement.textContent).toContain('Add today’s token');
    http.verify();
  });

  it('shows deterministic paper-entry readiness checks', async () => {
    TestBed.overrideProvider(AuthService, {
      useValue: { currentUser: () => of({ username: 'administrator', roles: ['Administrator'] }) },
    });
    TestBed.overrideProvider(GrowwTokenService, { useValue: { getStatus: () => NEVER } });
    TestBed.overrideProvider(PaperRiskService, {
      useValue: {
        getKillSwitch: () => NEVER, setKillSwitch: () => NEVER,
        getDailyLossOverride: () => NEVER, setDailyLossOverride: () => NEVER,
      },
    });
    TestBed.overrideProvider(TradingWorkspaceService, {
      useValue: {
        getMarket: () => of({
          instrument: 'NIFTY', exchange: 'NSE', timeframe: '1m', mode: 'Paper',
          feedStatus: 'Live', isLive: true, isFresh: true,
          lastMarketTimestampUtc: '2026-08-03T08:30:00Z', observedAtUtc: '2026-08-03T08:30:00Z',
          statusMessage: null, candles: [], overlays: [], evaluations: [{
            evaluationId: 'evaluation-1', candleTimeUtc: '2026-08-03T08:30:00Z',
            strategy: 'opening-range-breakout 1.0.0', outcome: 'NoSignal', currentPrice: 24600,
            openingRangeHigh: 24620, openingRangeLow: 24550, vwap: 24590,
            fastEma: 24601, slowEma: 24595, atrPercent: 0.3, relativeFuturesVolume: 0.62,
            regime: 'WeakBullishTrend', regimeBias: 'Buy', regimeConfidence: 0.49,
            failedConditions: ['Relative futures volume 0.62 is below 0.75.'], signalId: null,
            optionSymbol: null, optionType: null, optionExpiry: null, optionStrike: null,
            optionPremium: null, realisedPnl: null,
          }],
          paperAutomation: {
            status: 'WarmingUp', tradingPermitted: false, message: 'Waiting for confirmation.',
            observedAtUtc: '2026-08-03T08:30:00Z', tradesToday: 0, realisedPnl: 0,
            unrealisedPnl: 0, activeSignalId: null, activeDirection: null,
            activeQuantity: null, entryPrice: null, stopLoss: null, target: null,
            selectedOptionSymbol: null, selectedOptionType: null, selectedOptionExpiry: null,
            selectedOptionStrike: null, selectedOptionLotSize: null,
            currentOptionPrice: null,
            portfolioRisk: {
              openPositions: 0, capitalExposure: 0, openRiskAtStops: 0,
              dailyLossConsumed: 0, maximumDailyLoss: 5000,
              quoteUnavailablePositions: 0, reconciliationHealthy: true,
              observedAtUtc: '2026-08-03T08:30:00Z',
            },
            readinessChecks: [
              { code: 'history', label: 'Previous-session context', ready: true,
                detail: 'Previous close available' },
              { code: 'future', label: 'Nifty futures confirmation', ready: false,
                detail: '18/21 candles' },
            ],
          },
        }),
        updates$: () => NEVER,
        connect: () => Promise.resolve(),
        disconnect: () => Promise.resolve(),
      },
    });

    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/system/status').flush({
      mode: 'Paper', liveTradingAvailable: false, tradingEnabled: false,
      status: 'FoundationOnly', observedAtUtc: '2026-08-03T08:30:00Z',
    });
    await fixture.whenStable();
    fixture.detectChanges();

    const niftyButton = Array.from(fixture.nativeElement.querySelectorAll('button'))
      .find((button: unknown) => (button as HTMLButtonElement).textContent?.includes('Nifty')) as HTMLButtonElement;
    niftyButton.click();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('SESSION READINESS');
    expect(fixture.nativeElement.textContent).toContain('Previous-session context');
    expect(fixture.nativeElement.textContent).toContain('Nifty futures confirmation');
    expect(fixture.nativeElement.textContent).toContain('18/21 candles');
    expect(fixture.nativeElement.textContent).toContain('Ignore limit today');
    expect(fixture.nativeElement.textContent).toContain('RESEARCH LOGS');
    expect(fixture.nativeElement.textContent).not.toContain('Relative futures volume 0.62 is below 0.75.');
    const logsButton = Array.from(fixture.nativeElement.querySelectorAll('button'))
      .find((button: unknown) => (button as HTMLButtonElement).textContent?.includes('View logs')) as HTMLButtonElement;
    logsButton.click();
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Relative futures volume 0.62 is below 0.75.');
  });

  it('opens functional Sensex and Natural Gas workspaces', async () => {
    TestBed.overrideProvider(AuthService, {
      useValue: { currentUser: () => of({ username: 'administrator', roles: ['Administrator'] }) },
    });
    TestBed.overrideProvider(GrowwTokenService, { useValue: { getStatus: () => NEVER } });
    TestBed.overrideProvider(TradingWorkspaceService, {
      useValue: { getMarket: () => NEVER, updates$: () => NEVER,
        connect: () => Promise.resolve(), disconnect: () => Promise.resolve() },
    });
    TestBed.overrideProvider(PaperRiskService, { useValue: {
      getKillSwitch: () => NEVER, setKillSwitch: () => NEVER,
      getDailyLossOverride: () => NEVER, setDailyLossOverride: () => NEVER,
    }});
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/system/status').flush({
      mode: 'Paper', liveTradingAvailable: false, tradingEnabled: false,
      status: 'FoundationOnly', observedAtUtc: '2026-08-20T08:30:00Z',
    });
    await fixture.whenStable(); fixture.detectChanges();

    const sensexButton = Array.from(fixture.nativeElement.querySelectorAll('button'))
      .find((button: unknown) => (button as HTMLButtonElement).textContent?.includes('Sensex')) as HTMLButtonElement;
    sensexButton.click(); fixture.detectChanges();

    expect(window.location.pathname).toBe('/sensex');
    expect(fixture.nativeElement.textContent).toContain('Sensex command view');
    expect(fixture.nativeElement.textContent).toContain('LIVE TRADING WORKSPACE');
    expect(fixture.nativeElement.textContent).not.toContain('Feed integration pending');
  });
});
