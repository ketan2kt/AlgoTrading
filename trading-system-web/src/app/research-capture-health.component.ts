import {DatePipe} from '@angular/common';
import {HttpClient} from '@angular/common/http';
import {Component, DestroyRef, inject, signal} from '@angular/core';
import {takeUntilDestroyed} from '@angular/core/rxjs-interop';

interface CaptureHealth {
  date:string; checkedAt:string; master:string|null; preOpenMaster:boolean;
  feed:{phase:string; instruments:number; subscribedAtUtc:string|null; gapObserved:boolean; lastReceiptByMarket:Record<string,string>};
}
@Component({selector:'app-research-capture-health', imports:[DatePipe], styles:[`
  section{border:1px solid #8aae94;border-radius:.55rem;padding:.55rem .7rem;background:#ecf5ea;color:#173e29}
  button{margin-inline-start:.6rem;border:1px solid #528264;border-radius:.35rem;background:#f5fbf2;color:#16492d;padding:.25rem .55rem;cursor:pointer}
  p{margin:.35rem 0}small{display:block}
`], template:`
  <section class="readiness-panel" aria-label="Replay capture">
    <strong>Replay capture · three markets</strong>
    <button type="button" (click)="check()" [disabled]="busy()">{{busy()?'Checking…':'Check now'}}</button>
    @if(error()){<p role="alert">Capture check unavailable. No coverage can be inferred.</p>}
    @if(snapshot();as s){
      <p>{{s.date}} · {{s.checkedAt|date:'HH:mm:ss':'Asia/Kolkata'}} IST · Pre-open master: {{s.preOpenMaster?'recorded':s.master?'late':'not recorded'}}.
        Worker: {{s.feed.phase}} · {{s.feed.instruments}} instruments
        @if(s.feed.subscribedAtUtc){ · subscribed {{s.feed.subscribedAtUtc|date:'HH:mm:ss':'Asia/Kolkata'}} IST}
        @if(s.feed.gapObserved){ · gap observed}
      </p>
      <p>@for(m of markets;track m){ {{m}}: {{s.feed.lastReceiptByMarket[m] ? (s.feed.lastReceiptByMarket[m]|date:'HH:mm:ss':'Asia/Kolkata') : 'no receipt'}} · }</p>
      <small>Latest receipt on this worker, not proof of complete quotes. Use Replay after close to verify session eligibility.</small>
    }
  </section>`})
export class ResearchCaptureHealthComponent {
  private readonly http=inject(HttpClient);
  private readonly destroy=inject(DestroyRef);
  protected readonly markets=['NIFTY','SENSEX','BANKNIFTY'];
  protected readonly snapshot=signal<CaptureHealth|null>(null);
  protected readonly busy=signal(false);
  protected readonly error=signal(false);
  protected check():void {
    if(this.busy())return;
    this.busy.set(true);this.error.set(false);this.snapshot.set(null);
    this.http.get<CaptureHealth>('/api/research/capture-health').pipe(takeUntilDestroyed(this.destroy)).subscribe({
      next:s=>{this.snapshot.set(s);this.busy.set(false);},
      error:()=>{this.error.set(true);this.busy.set(false);}
    });
  }
}
