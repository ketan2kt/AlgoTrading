import { ChangeDetectorRef, Component, inject, OnDestroy, OnInit } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Subscription, interval } from 'rxjs';
import { LiveExecutionService, LiveExecutionStatus, UpstoxExecutionReadiness } from './live-execution.service';
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
  upstox: UpstoxExecutionReadiness | null = null;
  connecting = false;
  ngOnInit():void { this.loadLots(); this.loadLive(); this.requests.add(interval(5000).subscribe(()=>this.loadLive(true))); }
  ngOnDestroy():void { this.requests.unsubscribe(); }
  loadLots():void {
    if(this.loadingLots || this.saving) return;
    this.lots=null; this.loadingLots=true; this.lotMessage='';
    this.requests.add(this.sizes.get().subscribe({
      next:v=>{this.lots=v; this.loadingLots=false; this.view.markForCheck();},
      error:()=>{this.loadingLots=false; this.lotMessage='Saved lot sizes unavailable.'; this.view.markForCheck();}
    }));
  }
  loadLive(background=false):void {
    if(this.loadingLive || this.changing) return;
    if(!background) this.status=null;
    this.loadingLive=true; if(!background) this.liveMessage='';
    this.requests.add(this.live.getStatus().subscribe({
      next:v=>{this.status=v; this.loadingLive=false; this.view.markForCheck();},
      error:()=>{this.status=null; this.loadingLive=false; this.liveMessage='Live status unavailable.'; this.view.markForCheck();}
    }));
    this.requests.add(this.live.getUpstoxStatus().subscribe({
      next:v=>{this.upstox=v;this.view.markForCheck();},
      error:()=>{this.upstox=null;this.view.markForCheck();}
    }));
  }
  broker(key:string):'Groww'|'Upstox' {return this.status?.markets.find(m=>m.market===key)?.executionBroker ?? 'Groww';}
  entryStatus(key:string):string {return this.status?.markets.find(m=>m.market===key)?.entryStatus ?? 'Unknown';}
  entryReason(key:string):string {return this.status?.markets.find(m=>m.market===key)?.entryStatusReason ?? 'Live entry readiness unavailable.';}
  selectBroker(key:string, value:string):void {
    if(!this.status || this.loadingLive || this.armed(key)!==false || this.changing || (value!=='Groww' && value!=='Upstox')) return;
    this.changing=key; this.liveMessage='';
    this.requests.add(this.live.selectBroker(key,value).subscribe({
      next:v=>{this.status=v;this.changing='';this.liveMessage=key+' broker saved. Existing trades stay with their original broker; new entries remain OFF.';this.view.markForCheck();},
      error:()=>{this.status=null;this.changing='';this.liveMessage='Broker selection not confirmed. Reload live status.';this.view.markForCheck();}
    }));
  }
  connectUpstox():void {
    if(this.connecting || !this.upstox?.configured) return;
    this.connecting=true;
    this.requests.add(this.live.connectUpstox().subscribe({
      next:({url})=>{
        const target=new URL(url);
        if(target.origin==='https://api.upstox.com' && target.pathname==='/v2/login/authorization/dialog') window.location.assign(target.href);
        else {this.connecting=false;this.liveMessage='Unexpected authorization destination blocked.';this.view.markForCheck();}
      },
      error:()=>{this.connecting=false;this.liveMessage='Could not start Upstox authorization. Reload live status.';this.view.markForCheck();}
    }));
  }
  armed(key:string): boolean | null {return this.status?.markets?.find(m=>m.market===key)?.armed ?? null;}
  canSwitch(key:string):boolean {
    const ready=this.broker(key)==='Groww' || (!!this.upstox?.authorized && this.upstox.staticIpVerified && !this.upstox.sandbox && this.upstox.exchanges.includes(key==='SENSEX'?'BFO':'NFO'));
    return !!this.status?.buildEnabled && this.status.allowedMarkets.includes(key) && this.armed(key)!==null && !this.loadingLive && !this.changing && (this.armed(key)===true || ready);
  }
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
