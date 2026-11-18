import { TestBed } from '@angular/core/testing';
import { DailyProfitReviewComponent } from './daily-profit-review.component';
import type { DailyDiagnostics } from './paper-report.service';

describe('Daily profit review', () => {
  const report = {
    tradingDate: '2026-11-13', trades: [{ tradeId: 'one', market: 'BANKNIFTY', symbol: '61100CE',
      openedAtUtc: '2026-11-13T04:00:00Z', exitReason: 'FailedSetupExit', excursionCoverage: 'Observed gross' }],
    profitReview: {
      markets: ['NIFTY', 'SENSEX', 'BANKNIFTY'].map(market => ({ market, closedTrades: 1, openTrades: 0,
        netPnl: -250, costs: 50, comparablePeakTrades: 0, observedPeakSubtotal: null,
        grossGivebackSubtotal: null, positivePeakToNetLossTrades: null })),
      trades: [{ tradeId: 'one', entry: { status: 'Timing unverified', recordedAtUtc: null,
        extensionAtr: null, roomAtr: null, candleAgeSeconds: null, reasons: ['Missing receipt'] },
        grossGiveback: null, retainedPercent: null, profitEvidence: 'Not comparable',
        exitAssessment: 'Mixed follow-up', finalExitUnits: 60,
        followUps: [{ horizonMinutes: 5, observedAtUtc: '2026-11-13T04:15:00Z', price: 110, grossDifference: 360 }] }],
    },
  } as unknown as DailyDiagnostics;

  it('shows all markets, missing evidence and partial-size follow-ups without claiming replay savings', async () => {
    await TestBed.configureTestingModule({ imports: [DailyProfitReviewComponent] }).compileComponents();
    const fixture = TestBed.createComponent(DailyProfitReviewComponent);
    fixture.componentRef.setInput('report', report); fixture.detectChanges();
    const text = fixture.nativeElement.textContent;
    expect(text).toContain('Nifty'); expect(text).toContain('Sensex'); expect(text).toContain('Bank Nifty');
    expect(text).toContain('0 / 1'); expect(text).toContain('Timing unverified');
    expect(text).toContain('60 units'); expect(text).toContain('+₹360.00');
    expect(text).toContain('not alternative fills'); expect(text).not.toContain('0% gross retained');
    const dates: string[] = []; fixture.componentInstance.replay.subscribe(date => dates.push(date));
    fixture.nativeElement.querySelector('button').click(); expect(dates).toEqual(['2026-11-13']);
  });

  it('supports older responses and does not trigger network work or a replay on load', async () => {
    await TestBed.configureTestingModule({ imports: [DailyProfitReviewComponent] }).compileComponents();
    const fixture = TestBed.createComponent(DailyProfitReviewComponent);
    fixture.componentRef.setInput('report', { ...report, profitReview: null }); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Outcome attribution is unavailable');
  });

  it('distinguishes conditional break-even from a positive trail and shows the exit receipt', async () => {
    await TestBed.configureTestingModule({ imports: [DailyProfitReviewComponent] }).compileComponents();
    const fixture = TestBed.createComponent(DailyProfitReviewComponent);
    fixture.componentRef.setInput('report', { ...report, trades: [{ ...report.trades[0], protectionReceipts: [{
      atUtc: '2026-11-13T04:15:00Z', policy: 'BankNiftyConfirmedTrail', stage: 'Conditional break-even',
      floorNet: 0, stopPrice: 90, decisionBid: 95, triggered: true, version: 'v2'
    }] }] });
    fixture.detectChanges();
    const text = fixture.nativeElement.textContent;
    expect(text).toContain('Conditional break-even');
    expect(text).toContain('EXIT TRIGGERED');
    expect(text).toContain('Net floor ₹0.00');
    expect(text).toContain('Stop ₹90.00');
    expect(text).toContain('not the 70% trail');
  });
});
