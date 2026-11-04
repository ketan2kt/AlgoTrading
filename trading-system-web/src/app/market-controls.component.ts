import { ChangeDetectorRef, Component, inject, OnDestroy, OnInit } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { LiveExecutionService, LiveExecutionStatus } from './live-execution.service';
import { TradingLotsService, TradingLotConfiguration } from './trading-lots.service';

@Component({ selector: 'app-market-controls', imports: [FormsModule, DatePipe], templateUrl: './market-controls.component.html' })
export class MarketControlsComponent implements OnInit, OnDestroy {
  private readonly live = inject(LiveExecutionService);
  private readonly sizes = inject(TradingLotsService);
  private readonly view = inject(ChangeDetectorRef);
  private readonly requests = new Subscription();
  readonly rows = [{name:'Nifty', key:'NIFTY', field:'niftyLots'}, {name:'Sensex', key:'SENSEX', field:'sensexLots'}, {name:'Bank Nifty', key:'BANK-NIFTY', field:'bankNiftyLots'}] as const;
  lots: TradingLotConfiguration | null = null;
  status: LiveExecutionStatus | null = null;
  loadingLots = false;
  loadingLive = false;
  saving = false;
  changing = '';
  lotMessage = '';
  liveMessage = '';
  ngOnInit():void { this.loadLots(); this.loadLive(); }
  ngOnDestroy():void { this.requests.unsubscribe(); }
  loadLots():void {
    if(this.loadingLots || this.saving) return;
    this.lots=null; this.loadingLots=true; this.lotMessage='';
    this.requests.add(this.sizes.get().subscribe({
      next:v=>{this.lots=v; this.loadingLots=false; this.view.markForCheck();},
      error:()=>{this.loadingLots=false; this.lotMessage='Saved lot sizes unavailable.'; this.view.markForCheck();}
    }));
  }
  loadLive():void {
    if(this.loadingLive || this.changing) return;
    this.status=null; this.loadingLive=true; this.liveMessage='';
    this.requests.add(this.live.getStatus().subscribe({
      next:v=>{this.status=v; this.loadingLive=false; this.view.markForCheck();},
      error:()=>{this.loadingLive=false; this.liveMessage='Live status unavailable.'; this.view.markForCheck();}
    }));
  }
  armed(key:string): boolean | null {return this.status?.markets?.find(m=>m.market===key)?.armed ?? null;}
  canSwitch(key:string):boolean {return !!this.status?.buildEnabled && this.status.allowedMarkets.includes(key) && this.armed(key)!==null && !this.changing;}
  switch(key:string):void {
    if(!this.canSwitch(key)) return;
    this.changing=key; this.liveMessage='';
    this.requests.add(this.live.setArmed(key,!this.armed(key)).subscribe({
      next:v=>{this.status=v;this.changing='';this.liveMessage=key+' live-entry setting updated.';this.view.markForCheck();},
      error:()=>{this.status=null;this.changing='';this.liveMessage='Could not confirm live state. Reload before trying again.';this.view.markForCheck();}
    }));
  }
  save():void {
    if(!this.lots || this.saving) return;
    const values=this.rows.map(r=>this.lots![r.field]);
    if(values.some(n=>!Number.isInteger(n)||n<1||n>50)){this.lotMessage='Use whole lots from 1 to 50.';return;}
    this.saving=true;
    this.requests.add(this.sizes.save(values[0],values[1],values[2]).subscribe({
      next:v=>{this.lots=v;this.saving=false;this.lotMessage='Lot sizes saved.';this.view.markForCheck();},
      error:()=>{this.lots=null;this.saving=false;this.lotMessage='Save not confirmed. Reload saved sizes before retrying.';this.view.markForCheck();}
    }));
  }
}
