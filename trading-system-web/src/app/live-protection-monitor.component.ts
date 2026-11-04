import {DatePipe,DecimalPipe} from '@angular/common';
import {HttpClient} from '@angular/common/http';
import {Component,HostListener,inject,OnDestroy,OnInit,signal} from '@angular/core';
import {Subscription} from 'rxjs';

interface Row {id:string;market:string;symbol:string;lifecycle:string;paperUnits:number|null;liveUnits:number|null;
  desiredStop:number|null;stopOrderId:string|null;confirmedUnits:number|null;confirmedStop:number|null;
  confirmedAt:string|null;finding:string;needsAttention:boolean}
interface Snapshot {readAt:string;attention:number;rows:Row[]}

// Presentation only: regular Indian index desk hours, independent of browser time zone.
export function inProtectionDeskHours(now:Date):boolean {
  const ist=new Date(now.getTime()+330*60_000);
  const minutes=ist.getUTCHours()*60+ist.getUTCMinutes();
  return ist.getUTCDay()>0 && ist.getUTCDay()<6 && minutes>=555 && minutes<930;
}

@Component({selector:'app-live-protection-monitor',imports:[DatePipe,DecimalPipe],
  templateUrl:'./live-protection-monitor.component.html',styleUrl:'./live-protection-monitor.component.scss'})
export class LiveProtectionMonitorComponent implements OnInit,OnDestroy{
  private readonly http=inject(HttpClient);
  private request?:Subscription;
  private timer?:ReturnType<typeof setInterval>;
  protected readonly snapshot=signal<Snapshot|null>(null);
  protected readonly failed=signal(false);
  protected readonly busy=signal(false);
  protected readonly inSession=signal(inProtectionDeskHours(new Date()));
  private tick(readOnce=false){
    const active=inProtectionDeskHours(new Date());
    const changed=active!==this.inSession();
    this.inSession.set(active);
    if(active||changed||readOnce)this.refresh();
  }
  ngOnInit(){this.tick(true);this.timer=setInterval(()=>this.tick(),20_000);}
  ngOnDestroy(){clearInterval(this.timer);this.request?.unsubscribe();}
  @HostListener('document:visibilitychange') visibility(){
    if(document.hidden){this.request?.unsubscribe();this.busy.set(false);this.snapshot.set(null);}
    else this.tick(true);
  }
  protected refresh(){
    if(document.hidden||this.busy())return;
    this.busy.set(true);
    this.request=this.http.get<Snapshot>('/api/live-execution/protection-monitor',{timeout:12_000}).subscribe({
      next:r=>{this.snapshot.set(r);this.failed.set(false);this.busy.set(false);},
      error:()=>{this.snapshot.set(null);this.failed.set(true);this.busy.set(false);}
    });
  }
  protected open(dialog:HTMLDialogElement){dialog.showModal();this.refresh();}
}
