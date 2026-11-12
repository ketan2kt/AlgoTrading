import {DatePipe} from '@angular/common';
import {HttpClient} from '@angular/common/http';
import {Component, DestroyRef, Input, OnInit, inject, signal} from '@angular/core';
import {takeUntilDestroyed} from '@angular/core/rxjs-interop';
import {timer, timeout} from 'rxjs';
import {captureAlertText} from './capture-alert';

interface CaptureHealth {
  date:string; checkedAt:string; master:string|null; preOpenMaster:boolean;
  expectedNow:boolean; issues:string[];
  feed:{phase:string; instruments:number; subscribedAtUtc:string|null; gapObserved:boolean; lastReceiptByMarket:Record<string,string>; lastFailure?:string|null};
}
@Component({selector:'app-research-capture-health', imports:[DatePipe], styles:[`
  section{border:1px solid #8aae94;border-radius:.55rem;padding:.55rem .7rem;background:#ecf5ea;color:#173e29}
  button{margin-inline-start:.6rem;border:1px solid #528264;border-radius:.35rem;background:#f5fbf2;color:#16492d;padding:.25rem .55rem;cursor:pointer}
  p{margin:.35rem 0}small{display:block}
  .capture-warning{margin:.25rem .6rem;padding:.4rem .65rem;border:1px solid #b17d28;border-radius:.4rem;background:#fff1d2;color:#5d3905;font-size:.8rem}
  .capture-warning summary{cursor:pointer;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
  .capture-warning ul{margin:.4rem 0 0;padding-inline-start:1.25rem}
`], template:`
  @if(compact){
    @if(snapshot();as s){@if(s.expectedNow && s.issues.length){
      <details class="capture-warning">
        <summary><span role="status"><strong>Replay</strong> · {{alertText(s.issues)}} · {{s.checkedAt|date:'HH:mm:ss':'+0530'}}</span></summary>
        <ul>@for(issue of s.issues;track $index){<li>{{issue}}</li>}</ul>
      </details>
    }}
    @if(error() && snapshot()?.expectedNow){<div class="capture-warning" role="status"><strong>Replay</strong> · Status unavailable · Retrying</div>}
  } @else {
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
      @if(s.feed.lastFailure){<p>Last recorder failure: {{s.feed.lastFailure}}</p>}
      @if(s.issues.length){<p role="status">{{s.issues.join(' · ')}}</p>}
      <p>@for(m of markets;track m){ {{m}}: {{s.feed.lastReceiptByMarket[m] ? (s.feed.lastReceiptByMarket[m]|date:'HH:mm:ss':'Asia/Kolkata') : 'no receipt'}} · }</p>
      <small>Latest receipt on this worker, not proof of complete quotes. Use Replay after close to verify session eligibility.</small>
    }
  </section>}`})
export class ResearchCaptureHealthComponent implements OnInit {
  @Input() compact=false;
  private readonly http=inject(HttpClient);
  private readonly destroy=inject(DestroyRef);
  protected readonly alertText=captureAlertText;
  protected readonly markets=['NIFTY','SENSEX','BANKNIFTY'];
  protected readonly snapshot=signal<CaptureHealth|null>(null);
  protected readonly busy=signal(false);
  protected readonly error=signal(false);
  ngOnInit():void {
    if(this.compact) timer(0,30000).pipe(takeUntilDestroyed(this.destroy)).subscribe(()=>this.check());
  }
  protected check():void {
    if(this.busy())return;
    this.busy.set(true);this.error.set(false);
    this.http.get<CaptureHealth>('/api/research/capture-health').pipe(timeout(10000),takeUntilDestroyed(this.destroy)).subscribe({
      next:s=>{this.snapshot.set(s);this.busy.set(false);},
      error:()=>{this.error.set(true);this.busy.set(false);}
    });
  }
}
