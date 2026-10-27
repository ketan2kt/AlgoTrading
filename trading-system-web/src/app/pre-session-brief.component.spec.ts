import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { PreSessionBriefComponent } from './pre-session-brief.component';

describe('Pre-session brief', () => {
  beforeEach(() => TestBed.configureTestingModule({
    imports: [PreSessionBriefComponent],
    providers: [provideHttpClient(), provideHttpClientTesting()],
  }));

  it('shows missing capture and unavailable external inputs without inventing a bias', async () => {
    const fixture = TestBed.createComponent(PreSessionBriefComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne(request => request.url === '/api/market-analysis/pre-session').flush({
      sessionDate: '2026-10-27', generatedAtUtc: '2026-10-27T03:20:00Z',
      missingStages: ['nifty: PreOpen snapshot not captured', 'sensex: PreOpen snapshot not captured'],
      outcomes: [],
      scorecards: [{ market: 'nifty', reviewedSessions: 0, directionalGuesses: 0,
        correct: 0, incorrect: 0, accuracyPercent: null }],
      briefs: [{
        market: 'nifty', sessionDate: '2026-10-27', stage: 'Overnight',
        capturedAtUtc: '2026-10-27T03:15:00Z', bias: 'Unclear',
        headline: 'Opening direction needs confirmation after 9:15 IST.',
        judgement: 'Unclear', confidence: 'Low', judgementReason: 'No strong prior close.',
        previousOpen: 99,
        previousClose: 100, previousHigh: 102, previousLow: 98,
        previousSessionAtUtc: '2026-10-26T10:00:00Z', indicativePrice: null,
        indicativePriceAtUtc: null, callOiStrike: null, putOiStrike: null,
        optionExpiry: null, optionOiAtUtc: null,
        scenarios: ['Wait for the opening range.'],
        evidence: [{ name: 'GIFT Nifty', status: 'Unavailable', value: null,
          source: null, sourceAtUtc: null, note: 'No authorized source connected.' }],
        limitations: ['Read-only research.'],
      }],
    });
    await fixture.whenStable();
    fixture.detectChanges();
    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Missing capture');
    expect(text).toContain('Opening direction needs confirmation');
    expect(text).toContain('GIFT Nifty');
    expect(text).toContain('Unavailable');
    expect(text).toContain('No as-of morning snapshot');
    expect(text).toContain('not established');
    http.verify();
  });

  it('shows the factual outcome even if the morning snapshot was missed', async () => {
    const fixture = TestBed.createComponent(PreSessionBriefComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne(request => request.url === '/api/market-analysis/pre-session').flush({
      sessionDate: '2026-10-27', generatedAtUtc: '2026-10-27T10:30:00Z',
      missingStages: ['nifty: Overnight snapshot not captured'], briefs: [],
      outcomes: [{ market: 'nifty', sessionDate: '2026-10-27', open: 100,
        high: 110, low: 99, close: 107, recordedCandles: 300,
        lastCandleAtUtc: '2026-10-27T10:00:00Z', crossedPriorHigh: null,
        crossedPriorLow: null, actualDirection: 'Up', morningJudgement: 'Unavailable',
        assessment: 'Unscored', coverage: 'Sufficient', assessedAtUtc: '2026-10-27T10:15:00Z' }],
      scorecards: [],
    });
    await fixture.whenStable();
    fixture.detectChanges();
    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('No as-of morning snapshot');
    expect(text).toContain('After-session observation');
    expect(text).toContain('Unscored');
    http.verify();
  });
});
