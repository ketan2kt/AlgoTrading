import {DatePipe,DecimalPipe} from '@angular/common';
import {HttpClient} from '@angular/common/http';
import {Component,HostListener,inject,OnDestroy,OnInit,signal} from '@angular/core';
import {Subscription,switchMap} from 'rxjs';

interface Row {id:string;market:string;symbol:string;lifecycle:string;paperUnits:number|null;liveUnits:number|null;
  desiredStop:number|null;stopOrderId:string|null;confirmedUnits:number|null;confirmedStop:number|null;
  confirmedAt:string|null;finding:string;needsAttention:boolean;sessionDate:string;reviewVersion:string|null;reviewedAt:string|null}
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
  private write?:Subscription;
  protected readonly confirming=signal<string|null>(null);
  protected readonly saving=signal(false);
  protected readonly feedback=signal('');
  protected hasReviewed(s:Snapshot){return s.rows.some(r=>!!r.reviewedAt);}
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
  ngOnDestroy(){clearInterval(this.timer);this.request?.unsubscribe();this.write?.unsubscribe();}
  @HostListener('document:visibilitychange') visibility(){
    if(document.hidden){this.request?.unsubscribe();this.busy.set(false);this.snapshot.set(null);}
    else this.tick(true);
  }
  protected refresh(){
    if(document.hidden||this.busy()||this.saving())return;
    this.busy.set(true);
    this.request=this.http.get<Snapshot>('/api/live-execution/protection-monitor',{timeout:12_000}).subscribe({
      next:r=>{this.snapshot.set(r);this.failed.set(false);this.busy.set(false);},
      error:()=>{this.snapshot.set(null);this.failed.set(true);this.busy.set(false);}
    });
  }
  protected markReviewed(row:Row){
    if(this.saving() || !row.reviewVersion || row.reviewVersion!==this.confirming())return;
    this.request?.unsubscribe();this.busy.set(false);this.saving.set(true);this.feedback.set('');
    this.write=this.http.get<{token:string}>('/api/security/antiforgery-token',{timeout:12_000}).pipe(switchMap(csrf=>
      this.http.post<void>('/api/live-execution/protection-monitor/'+row.id+'/review',{version:row.reviewVersion},
        {headers:{'X-CSRF-TOKEN':csrf.token},timeout:12_000}))).subscribe({
      next:()=>this.finishReview('Reminder marked reviewed. No order was changed.'),
      error:()=>this.finishReview('Review not confirmed. Check the refreshed record before trying again.')
    });
  }
  private finishReview(message:string){this.saving.set(false);this.confirming.set(null);this.feedback.set(message);this.refresh();}
  protected open(dialog:HTMLDialogElement){this.confirming.set(null);this.feedback.set('');dialog.showModal();this.refresh();}
}
