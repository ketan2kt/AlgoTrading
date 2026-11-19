import {DatePipe} from '@angular/common';
import {HttpClient} from '@angular/common/http';
import {Component, DestroyRef, Input, OnInit, computed, inject, signal} from '@angular/core';
import {takeUntilDestroyed} from '@angular/core/rxjs-interop';
import {timer, timeout} from 'rxjs';
import {CaptureIndicatorHealth, captureIndicator} from './capture-alert';

interface CaptureHealth extends CaptureIndicatorHealth {
  date:string; checkedAt:string; master:string|null; preOpenMaster:boolean;
  expectedNow:boolean; issues:string[];
  feed:{phase:string; instruments:number; subscribedAtUtc:string|null; gapObserved:boolean; lastReceiptByMarket:Record<string,string>; lastFailure?:string|null};
}
@Component({selector:'app-research-capture-health', imports:[DatePipe], styles:[`
  section{border:1px solid #8aae94;border-radius:.55rem;padding:.55rem .7rem;background:#ecf5ea;color:#173e29}
  button{margin-inline-start:.6rem;border:1px solid #528264;border-radius:.35rem;background:#f5fbf2;color:#16492d;padding:.25rem .55rem;cursor:pointer}
  p{margin:.35rem 0}small{display:block}
  :host{display:block;min-width:0}
  .capture-warning{--border:#b17d28;--bg:#fff1d2;--open:#ffe7af;position:relative;margin:0;border:1px solid var(--border);border-radius:.35rem;background:var(--bg);color:#5d3905;font-size:.72rem}
  .capture-warning summary{cursor:pointer;white-space:nowrap;padding:4px 8px}
  .capture-warning summary:focus-visible{outline:2px solid #624710;outline-offset:2px}
  .capture-warning[open] summary{background:var(--open);border-radius:.35rem}
  .capture-warning__body{position:absolute;top:calc(100% + 6px);left:0;z-index:20;width:min(520px,calc(100vw - 32px));box-sizing:border-box;padding:12px;border:1px solid var(--border);border-radius:.5rem;background:var(--bg);box-shadow:0 8px 24px #173e2926;max-height:50vh;overflow:auto;overflow-wrap:anywhere;white-space:normal}
  .capture-warning__body small{font-size:.7rem}
  .capture-warning ul{margin:.5rem 0 0;padding-inline-start:1.25rem}
  .capture-warning li+li{margin-top:.4rem}
  .capture-warning--healthy{--border:#43845b;--bg:#e3f4e8;--open:#ccebd5;color:#14532d}
  .capture-warning--critical{--border:#bc4545;--bg:#fde8e8;--open:#fbd0d0;color:#8b1e1e}
  .capture-warning--idle{--border:#89958d;--bg:#edf1ee;--open:#dce4df;color:#44534a}
  .capture-dot{display:inline-block;width:.5rem;height:.5rem;border-radius:50%;background:currentColor;margin-inline-end:.35rem}
  @media(max-width:560px){.capture-warning{position:static}.capture-warning__body{left:8px;right:8px;top:calc(100% + 4px);width:auto}}
`], template:`
  @if(compact){
      <details class="capture-warning" [class.capture-warning--healthy]="indicator().severity === 'healthy'"
        [class.capture-warning--critical]="indicator().severity === 'critical'"
        [class.capture-warning--idle]="indicator().severity === 'idle'"
        #warning (keydown.escape)="warning.open=false">
        <summary><span role="status"><span class="capture-dot" aria-hidden="true"></span><strong>Replay</strong> · {{indicator().text}}</span></summary>
        <div class="capture-warning__body">
          @if(snapshot();as s){
          <small>Checked {{s.checkedAt|date:'HH:mm:ss':'+0530'}} IST @if(error()){ · Retrying; last known issues below}</small>
          <ul>@for(issue of s.issues;track $index){<li>{{issue}}</li>}</ul>
          @if(!s.issues.length){<p>{{indicator().severity === 'healthy' ? 'No recording issues reported for Nifty, Sensex or Bank Nifty.' : indicator().severity === 'idle' ? 'Capture is not scheduled now.' : 'Recording status needs attention; check the latest status time.'}}</p>}
          <small>Current recording status, not a guarantee of full-session replay coverage.</small>
          } @else {<p>{{error() ? 'Status check failed; retrying automatically.' : 'Checking recording status…'}}</p>}
        </div>
      </details>
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
  protected readonly markets=['NIFTY','SENSEX','BANKNIFTY'];
  protected readonly snapshot=signal<CaptureHealth|null>(null);
  protected readonly busy=signal(false);
  protected readonly error=signal(false);
  private readonly checkedClock=signal(Date.now());
  protected readonly indicator=computed(()=>captureIndicator(this.snapshot(),this.error(),this.checkedClock()));
  ngOnInit():void {
    if(this.compact) timer(0,30000).pipe(takeUntilDestroyed(this.destroy)).subscribe(()=>this.check());
  }
  protected check():void {
    this.checkedClock.set(Date.now());
    if(this.busy())return;
    this.busy.set(true);
    this.http.get<CaptureHealth>('/api/research/capture-health').pipe(timeout(10000),takeUntilDestroyed(this.destroy)).subscribe({
      next:s=>{this.snapshot.set(s);this.checkedClock.set(Date.now());this.error.set(false);this.busy.set(false);},
      error:()=>{this.error.set(true);this.busy.set(false);}
    });
  }
}
