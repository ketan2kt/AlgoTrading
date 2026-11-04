import {DatePipe,DecimalPipe} from '@angular/common';
import {HttpClient} from '@angular/common/http';
import {Component,HostListener,inject,OnDestroy,OnInit,signal} from '@angular/core';
import {Subscription} from 'rxjs';

interface Row {id:string;market:string;symbol:string;lifecycle:string;paperUnits:number|null;liveUnits:number|null;
  desiredStop:number|null;stopOrderId:string|null;confirmedUnits:number|null;confirmedStop:number|null;
  confirmedAt:string|null;finding:string;needsAttention:boolean}
interface Snapshot {readAt:string;attention:number;rows:Row[]}

@Component({selector:'app-live-protection-monitor',imports:[DatePipe,DecimalPipe],
  templateUrl:'./live-protection-monitor.component.html',styleUrl:'./live-protection-monitor.component.scss'})
export class LiveProtectionMonitorComponent implements OnInit,OnDestroy{
  private readonly http=inject(HttpClient);
  private request?:Subscription;
  private timer?:ReturnType<typeof setInterval>;
  protected readonly snapshot=signal<Snapshot|null>(null);
  protected readonly failed=signal(false);
  protected readonly busy=signal(false);
  ngOnInit(){this.refresh();this.timer=setInterval(()=>this.refresh(),20_000);}
  ngOnDestroy(){clearInterval(this.timer);this.request?.unsubscribe();}
  @HostListener('document:visibilitychange') visibility(){
    if(document.hidden){this.request?.unsubscribe();this.busy.set(false);this.snapshot.set(null);}
    else this.refresh();
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
