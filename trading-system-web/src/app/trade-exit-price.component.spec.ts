import {TestBed} from '@angular/core/testing';
import {TradeExitPriceComponent} from './trade-exit-price.component';
import {WorkspaceTradeOverlay} from './trading-workspace';

describe('trade exit presentation',()=>{
  const closed = () => ({exitTimeUtc:'2026-11-19T06:31:40Z',exitPrice:191.2,currentOptionPrice:191.2,
    realisedPnl:7614.62,grossRealisedPnl:7978.75,realisedCosts:364.13,
    exitBreakdown:{averagePrice:192.675,complete:true,fills:[
      {kind:'Partial',quantity:325,price:194.15},{kind:'Final',quantity:325,price:191.2}]}
  } as WorkspaceTradeOverlay);
  it.each(['NIFTY','SENSEX','BANKNIFTY'])('shows weighted exit and collapsed fill details for %s',market=>{
    const trade={...closed(),executionInstrument:market+'CE'};
    const before=JSON.stringify(trade);
    const f=TestBed.createComponent(TradeExitPriceComponent);f.componentRef.setInput('trade',trade);f.detectChanges();
    const el=f.nativeElement as HTMLElement;
    expect(el.textContent).toContain('Avg exit premium');
    expect(el.querySelector('strong')?.textContent).toBe('192.675');
    const details=el.querySelector('details')!;
    expect(details.open).toBe(false);
    details.querySelector('summary')!.click();
    expect(details.open).toBe(true);
    expect(details.textContent).toContain('Partial · 325 units × ₹194.15');
    expect(details.textContent).toContain('Final · 325 units × ₹191.20');
    expect(JSON.stringify(trade)).toBe(before);
  });
  it('keeps current premium for active trades even if a partial was booked',()=>{
    const f=TestBed.createComponent(TradeExitPriceComponent);
    f.componentRef.setInput('trade',{...closed(),exitTimeUtc:null,exitPrice:null});f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Current premium');
    expect(f.nativeElement.querySelector('details')).toBeNull();
  });
  it('labels legacy or incomplete evidence as final exit, never average',()=>{
    const f=TestBed.createComponent(TradeExitPriceComponent);
    f.componentRef.setInput('trade',{...closed(),exitBreakdown:null});f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Final exit premium');
    expect(f.nativeElement.textContent).toContain('Full fill breakdown unavailable');
    expect(f.nativeElement.querySelector('strong').textContent).toBe('191.20');
  });
});
