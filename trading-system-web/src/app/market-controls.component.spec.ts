import { TestBed } from '@angular/core/testing';
import { Subject, of } from 'rxjs';
import { MarketControlsComponent } from './market-controls.component';
import { TradingLotsService, TradingLotConfiguration } from './trading-lots.service';
import { LiveExecutionService, LiveExecutionStatus } from './live-execution.service';

describe('market controls',()=>{
  it('never presents missing server state as OFF or allows saving fallback lots',()=>{
    const lots=new Subject<TradingLotConfiguration>(), live=new Subject<LiveExecutionStatus>();
    TestBed.configureTestingModule({imports:[MarketControlsComponent],providers:[
      {provide:TradingLotsService,useValue:{get:()=>lots}},
      {provide:LiveExecutionService,useValue:{getStatus:()=>live,getUpstoxStatus:()=>of(null)}}]});
    const fixture=TestBed.createComponent(MarketControlsComponent); fixture.detectChanges();
    const root=fixture.nativeElement as HTMLElement;
    expect(root.querySelectorAll('input').length).toBe(0);
    expect(Array.from(root.querySelectorAll('.market-live-toggle')).some(b=>b.textContent?.includes('entries OFF'))).toBe(false);
    lots.error(new Error('unavailable'));live.error(new Error('unavailable'));fixture.detectChanges();
    expect(root.querySelector<HTMLButtonElement>('.market-live-toggle')?.disabled).toBe(true);
    expect(Array.from(root.querySelectorAll('button')).find(b=>b.textContent?.includes('Save lot sizes'))?.disabled).toBe(true);
    expect(root.textContent).toContain('Status unavailable');
  });

  it('blocks broker changes while armed and blocks unready Upstox entries, but always permits OFF',()=>{
    TestBed.configureTestingModule({imports:[MarketControlsComponent],providers:[
      {provide:TradingLotsService,useValue:{}}, {provide:LiveExecutionService,useValue:{}}]});
    const c=TestBed.createComponent(MarketControlsComponent).componentInstance;
    c.status={buildEnabled:true,markets:[{market:'NIFTY',armed:false,changedAtUtc:null,changedBy:null,executionBroker:'Upstox'}],
      allowedMarkets:['NIFTY'],mirrorsPaperQuantity:true,controlledBrokerTestCompleted:false,
      latestIntentMarket:null,latestIntentStatus:null,latestIntentError:null,latestIntentAtUtc:null};
    expect(c.canSwitch('NIFTY')).toBe(false);
    c.status.markets[0].armed=true;
    expect(c.canSwitch('NIFTY')).toBe(true);
    c.selectBroker('NIFTY','Groww');
    expect(c.broker('NIFTY')).toBe('Upstox');
    c.status.markets[0].armed=false;
    c.upstox={configured:true,authorized:true,staticIpVerified:true,sandbox:false,expiresAtUtc:null,exchanges:['NFO'],message:''};
    expect(c.canSwitch('NIFTY')).toBe(true);
  });
});
