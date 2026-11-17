import { TestBed } from '@angular/core/testing';
import { DailyCapitalSummaryComponent } from './daily-capital-summary.component';

describe('Daily capital summary', () => {
  it('shows simultaneous capital, IST peak time and after-cost return including Hero Zero', () => {
    const fixture = TestBed.createComponent(DailyCapitalSummaryComponent);
    fixture.componentRef.setInput('summary', {netRealisedPnl:42093.47,peakCapitalUsed:252000,
      peakAtUtc:'2026-11-17T04:39:04Z',returnPercent:16.7038,status:'Verified',note:'Adjusted for scale-ins and partial exits.'});
    fixture.detectChanges();
    const text = fixture.nativeElement.textContent;
    expect(text).toContain('252,000.00');
    expect(text).toContain('10:09:04 IST');
    expect(text).toContain('16.70%');
    expect(text).toContain('includes Hero Zero');
    expect(fixture.nativeElement.querySelector('.good')).not.toBeNull();
  });

  it('shows missing evidence rather than a zero return', () => {
    const fixture = TestBed.createComponent(DailyCapitalSummaryComponent);
    fixture.componentRef.setInput('summary', {peakCapitalUsed:null,returnPercent:null,status:'Incomplete',note:'Fill evidence incomplete.'});
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Fill evidence incomplete');
    expect(fixture.nativeElement.textContent).not.toContain('0.00%');
    expect(fixture.nativeElement.querySelectorAll('strong')[0].textContent).toBe('—');
  });

  it('renders negative return and handles no trades', () => {
    const fixture = TestBed.createComponent(DailyCapitalSummaryComponent);
    fixture.componentRef.setInput('summary', {peakCapitalUsed:1000,returnPercent:-12,status:'Verified'});
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.bad').textContent).toBe('-12.00%');
    fixture.componentRef.setInput('summary', {peakCapitalUsed:0,returnPercent:null,status:'NoTrades'});
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('No trades');
    expect(fixture.nativeElement.textContent).not.toContain('0.00%');
  });
});
