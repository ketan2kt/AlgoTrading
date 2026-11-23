import {TestBed} from '@angular/core/testing';
import {CompactTradeLedgerComponent} from './compact-trade-ledger.component';
import {WorkspaceTradeOverlay} from './trading-workspace';

describe('compact trade ledger',()=>{
  const trade=()=>({signalId:'t1',signalTimeUtc:'2026-11-23T09:10:03Z',entryTimeUtc:'2026-11-23T09:10:03Z',exitTimeUtc:'2026-11-23T09:25:41Z',
    lifecycleStatus:'BankNiftyConfirmedTrail',fillPrice:1290,exitPrice:1345.85,currentOptionPrice:1345.85,
    executionStrike:62900,executionInstrumentType:'PutOption',quantity:150,executionExpiry:'2026-12-29',executionStopLoss:1349.05,executionTarget:1419,
    realisedPnl:10332.39,grossRealisedPnl:10906.5,realisedCosts:574.11,maximumFavourablePnl:16509,maximumAdverseLoss:1050,
    exitBreakdown:{complete:true,averagePrice:1362.71,fills:[{kind:'Partial',quantity:60,price:1388},{kind:'Final',quantity:90,price:1345.85}]}
  } as WorkspaceTradeOverlay);
  it.each(['nifty','sensex','bank-nifty'])('retains every column and compact fills for %s',market=>{
    const f=TestBed.createComponent(CompactTradeLedgerComponent),t=trade(),before=JSON.stringify(t);
    f.componentRef.setInput('trades',[t]);f.componentRef.setInput('market',market);f.detectChanges();
    const el=f.nativeElement as HTMLElement;
    expect([...el.querySelectorAll('th')].map(x=>x.textContent)).toEqual(['Status','Net P&L','Contract','E','Avg X / Now','SL','Tgt','Qty','Exp','Gross','Costs','Peak','MAE','In','Out']);
    const cells=el.querySelectorAll('tbody tr:first-child > td');
    expect(cells[1].textContent).toContain('+10,332.39');
    expect(cells[9].textContent).toBe('+10,907');expect(cells[10].textContent).toBe('574');
    expect(cells[11].textContent).toBe('16,509');expect(cells[12].textContent).toBe('1,050');
    expect(cells[13].textContent).toBe('14:40:03');expect(cells[14].textContent).toBe('14:55:41');
    expect(el.querySelector('.session-heading')?.textContent).toContain('23 Nov 2026');
    const details=el.querySelector('details')!;expect(details.open).toBe(false);details.querySelector('summary')!.click();expect(details.open).toBe(true);
    expect(details.textContent).toContain('Partial · 60 units × ₹1,388.00');
    (el.querySelector('.status') as HTMLButtonElement).click();f.detectChanges();
    expect(el.querySelector('.status-detail')?.textContent).toContain('BankNiftyConfirmedTrail');
    expect(JSON.stringify(t)).toBe(before);
  });
  it('uses the open net estimate including partials, then the final net in the same cell',()=>{
    const f=TestBed.createComponent(CompactTradeLedgerComponent);
    const t={...trade(),exitTimeUtc:null,exitPrice:null,lifecycleStatus:'Active',realisedPnl:1000,unrealisedPnl:500,
      openPnlEstimate:{gross:1600,costs:200,net:1400}};
    f.componentRef.setInput('trades',[t]);f.detectChanges();
    const el=f.nativeElement as HTMLElement;
    expect(el.querySelector('.net')?.textContent).toBe('+1,400.00');
    expect(el.querySelectorAll('th')).toHaveLength(16);
    let exited:WorkspaceTradeOverlay|undefined;f.componentInstance.exitTrade.subscribe(t=>exited=t);
    (el.querySelector('td:last-child button') as HTMLButtonElement).click();expect(exited).toBe(t);
    f.componentRef.setInput('cached',true);f.detectChanges();expect((el.querySelector('td:last-child button') as HTMLButtonElement).disabled).toBe(true);
    f.componentRef.setInput('trades',[{...t,exitTimeUtc:'2026-11-23T09:25:41Z',exitPrice:1345.85,lifecycleStatus:'StopLossHit',realisedPnl:1200}]);f.detectChanges();
    expect(el.querySelector('.net')?.textContent).toBe('+1,200.00');expect(el.querySelectorAll('th')).toHaveLength(15);
  });
  it('does not present gross or missing estimates as net zero; groups dates and preserves unknown reasons',()=>{
    const f=TestBed.createComponent(CompactTradeLedgerComponent);
    f.componentRef.setInput('trades',[{...trade(),exitTimeUtc:null,exitPrice:null,lifecycleStatus:'Active · quote unavailable',unrealisedPnl:1234},
      {...trade(),signalId:'t2',entryTimeUtc:'2026-11-22T08:00:00Z',lifecycleStatus:'NewExitReason',realisedPnl:-101.25}]);f.detectChanges();
    const el=f.nativeElement as HTMLElement;
    expect(el.querySelector('.net')?.textContent).toBe('—');expect(el.querySelectorAll('.session-heading')).toHaveLength(2);
    expect(el.textContent).toContain('New Exit Reason');expect(el.textContent).toContain('−101.25');
  });
});
