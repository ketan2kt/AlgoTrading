import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { MarketControlsComponent } from './market-controls.component';
import { TradingLotsService, TradingLotConfiguration } from './trading-lots.service';
import { LiveExecutionService, LiveExecutionStatus } from './live-execution.service';

describe('market controls',()=>{
  it('never presents missing server state as OFF or allows saving fallback lots',()=>{
    const lots=new Subject<TradingLotConfiguration>(), live=new Subject<LiveExecutionStatus>();
    TestBed.configureTestingModule({imports:[MarketControlsComponent],providers:[
      {provide:TradingLotsService,useValue:{get:()=>lots}},
      {provide:LiveExecutionService,useValue:{getStatus:()=>live}}]});
    const fixture=TestBed.createComponent(MarketControlsComponent); fixture.detectChanges();
    const root=fixture.nativeElement as HTMLElement;
    expect(root.querySelectorAll('input').length).toBe(0);
    expect(root.textContent).not.toContain('entries OFF');
    lots.error(new Error('unavailable'));live.error(new Error('unavailable'));fixture.detectChanges();
    expect(root.querySelector<HTMLButtonElement>('.market-live-toggle')?.disabled).toBe(true);
    expect(Array.from(root.querySelectorAll('button')).find(b=>b.textContent?.includes('Save lot sizes'))?.disabled).toBe(true);
    expect(root.textContent).toContain('Status unavailable');
  });
});
