import { DatePipe, DecimalPipe, PercentPipe } from '@angular/common';
import { ChangeDetectorRef, Component, inject, Input, OnInit, OnDestroy, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import type { GapReplayJob, HistoricalGapAwareReport, HistoricalGapComparisonReport, ReplayTrial } from './historical-replay.service';
import { catchError, exhaustMap, of, Subscription, timer } from 'rxjs';
import { HistoricalNormalReplayReport, HistoricalNormalReplayRunSummary, HistoricalPolicyBuild, HistoricalReplayCohortCoverage, HistoricalReplayResearchReport, HistoricalReplayService } from './historical-replay.service';
@Component({selector:'app-replay-panel', imports:[DatePipe,DecimalPipe,PercentPipe,FormsModule], templateUrl:'./replay-panel.component.html', styleUrl:'./replay-panel.component.scss'})
export class ReplayPanelComponent implements OnInit, OnDestroy {
  readonly closed=output<void>();
  @Input() initialDate='';
  private readonly subscriptions=new Subscription();
  private readonly historicalReplayService=inject(HistoricalReplayService);
  private readonly view=inject(ChangeDetectorRef);
  private refreshView():void {this.view.markForCheck();}
  ngOnDestroy():void {this.subscriptions.unsubscribe();}
  protected replayLoading = false;
  protected replayError = '';
  protected replayReport: HistoricalReplayResearchReport | null = null;
  protected normalReplayReport: HistoricalNormalReplayReport | null = null;
  protected gapComparisonReport: HistoricalGapComparisonReport | null = null;
  protected normalReplayBusy = false;
  protected normalReplayError = '';
  protected normalReplayStatusText = '';
  protected gapAwareBusy = false;
  protected gapAwareError = '';
  protected gapAwareReport: HistoricalGapAwareReport | null = null;
  protected gapAwareStatusText = '';
  private readonly replayJobKey = 'sarthi.gap-replay.job';
  private readonly replayRequestKey = 'sarthi.gap-replay.pending';
  private readonly normalReplayJobKey = 'sarthi.normal-replay.job';
  private readonly normalReplayRequestKey = 'sarthi.normal-replay.pending';
  private pendingReplay?:{date:string;market:string;id:string};
  private gapWatch?:Subscription;
  protected normalReplayStopPercent = 10;
  protected normalReplayTrial:ReplayTrial='control';
  protected replayPolicyBuilds:HistoricalPolicyBuild[]=[];
  protected replayBaselineBuild='';
  protected replayProposalBuild='';
  protected replaySavedRuns:HistoricalNormalReplayRunSummary[]=[];
  protected replayFrozenInputRun='';
  protected replayCohort: HistoricalReplayCohortCoverage | null = null;
  protected replayCohortBusy = false;
  protected replayCohortError = '';
  protected replayFrom = this.isoDateDaysAgo(7);
  protected replayTo = this.isoDateDaysAgo(1);
  protected replayMarket = 'nifty';
  protected replayStopPercent = 0.10;
  protected replayTargetPercent = 0.15;
  protected replayBackfillBusy = false;
  protected replayBackfillMessage = '';
  protected replayIndexBusy = false;
  protected replayIndexMessage = '';
  protected replayFutureBusy = false;
  protected replayFutureMessage = '';
  protected replayCoverageBusy = false;
  protected replayCoverageMessage = '';
  protected replayExpiries: string[] = [];
  protected replayExpiry = '';
  protected replayUniversePage = 0;
  protected replayDiscoveryBusy = false;
  protected replayUniverseBusy = false;
  protected replayUniverseMessage = '';
  private isoDateDaysAgo(days:number):string {
    const value=new Date(Date.now()-days*86400000);
    return new Intl.DateTimeFormat('en-CA',{timeZone:'Asia/Kolkata',year:'numeric',month:'2-digit',day:'2-digit'}).format(value);
  }
  ngOnInit():void {
    if(this.initialDate){this.replayFrom=this.initialDate;this.replayTo=this.initialDate;}
    const previous = this.savedGapJob();
    const pending=this.savedReplayRequest();
    if(pending) {
      this.replayTo=pending.date;
      this.gapAwareMarket=pending.market;
      this.runGapAware();
    } else if(previous) this.watchGapJob(previous);
    this.resumeNormalJob();
    this.loadSavedNormalRuns();
    this.subscriptions.add(this.historicalReplayService.policyBuilds().subscribe({
      next:value=>{this.replayPolicyBuilds=value;this.refreshView();},
      error:()=>{this.normalReplayError='Unable to verify saved strategy builds. No older build will be substituted.';this.refreshView();},
    }));
  }
  private loadSavedNormalRuns():void {
    this.subscriptions.add(this.historicalReplayService.savedNormalRuns().subscribe({
      next:value=>{this.replaySavedRuns=value;this.refreshView();},
      error:()=>{this.normalReplayError='Unable to read saved replay runs.';this.refreshView();},
    }));
  }
  protected showSavedNormalReport():void {
    if(!this.replayFrozenInputRun)return;
    this.gapComparisonReport=null;
    this.normalReplayBusy=true;this.normalReplayError='';
    this.subscriptions.add(this.historicalReplayService.savedNormalReport(this.replayFrozenInputRun).subscribe({
      next:value=>{this.normalReplayReport=value;this.normalReplayBusy=false;this.refreshView();},
      error:()=>{this.normalReplayBusy=false;this.normalReplayError='Saved replay report is unavailable or failed integrity validation.';this.refreshView();},
    }));
  }
  protected closeHistoricalReplay():void { this.closed.emit(); }
  protected gapAwareMarket = '';
  protected runGapAware():void {
    if (!this.replayTo || this.normalReplayBusy || this.gapAwareBusy) return;
    const pending=this.savedReplayRequest();
    const request=pending?.date===this.replayTo && pending.market===this.gapAwareMarket ? pending :
      {date:this.replayTo,market:this.gapAwareMarket,id:crypto.randomUUID()};
    this.savedReplayRequest(request);
    this.gapAwareBusy=true;this.gapAwareError='';this.gapAwareReport=null;this.gapAwareStatusText='Saving replay request…';
    this.subscriptions.add(this.historicalReplayService.gapAware(request.date,request.market,request.id).subscribe({
      next:job=>{this.savedGapJob(job.id);this.savedReplayRequest(null);this.watchGapJob(job.id,job);},
      error:e=>{if(e.status>=400 && e.status<500 && e.status!==429) this.savedReplayRequest(null);
        this.gapAwareError=typeof e?.error==='string'?e.error:'The website did not confirm the replay request. Retry the same session and market to reconnect if it was queued.';
        this.gapAwareBusy=false;this.refreshView();},
    }));
  }
  private savedReplayRequest(request?:{date:string;market:string;id:string}|null) {
    if(request!==undefined) this.pendingReplay=request??undefined;
    try {
      if(request===null) localStorage.removeItem(this.replayRequestKey);
      else if(request) localStorage.setItem(this.replayRequestKey,JSON.stringify(request));
      else return (JSON.parse(localStorage.getItem(this.replayRequestKey)??'null') as typeof this.pendingReplay)??this.pendingReplay;
    } catch { /* Keep the pending request in memory if storage is unavailable. */ }
    return this.pendingReplay;
  }
  private savedGapJob(id?:string):string|null {
    try {
      if(id === '') localStorage.removeItem(this.replayJobKey);
      else if(id) localStorage.setItem(this.replayJobKey,id);
      return localStorage.getItem(this.replayJobKey);
    } catch { return null; }
  }
  private watchGapJob(id:string, initial?:GapReplayJob):void {
    this.gapWatch?.unsubscribe();
    this.gapAwareBusy=true;
    this.gapAwareStatusText=initial ? `${initial.date} · ${initial.status}` : 'Reconnecting to saved replay…';
    this.gapWatch=timer(0,5000).pipe(exhaustMap(()=>this.historicalReplayService.gapAwareStatus(id).pipe(
      catchError(e=>of(e.status===404?'missing' as const:e.status===401||e.status===403?'sign-in' as const:null))
    ))).subscribe(job=>{
      if(job==='missing'||job==='sign-in') {
        if(job==='missing') this.savedGapJob('');
        this.gapAwareError=job==='missing'?'Saved replay job is no longer available.':'Sign in and reopen Historical Replay to resume watching the saved job.';
        this.gapAwareBusy=false;this.gapWatch?.unsubscribe();
      } else if(!job) this.gapAwareStatusText='Connection interrupted. Reconnecting to the saved replay…';
      else if(job.status==='Completed'||job.status==='Failed') {
        this.gapAwareReport=job.report;
        this.gapAwareError=job.error??'';
        this.gapAwareBusy=false;this.gapWatch?.unsubscribe();
      } else this.gapAwareStatusText=`${job.date} · ${job.status}. You can close this panel and come back.`;
      this.refreshView();
    });
    this.subscriptions.add(this.gapWatch);
  }
  protected verifyPaperSession():void {
    if(!this.replayTo || this.normalReplayBusy || this.gapAwareBusy)return;
    this.submitNormalJob(['paper',this.replayTo], id => this.historicalReplayService.comparePaperSession(this.replayTo,id));
  }
  protected testRunnerOnControl(report:HistoricalNormalReplayReport):void {
    if(!report.verifiedControlWithFills || !report.savedRunId || this.normalReplayBusy)return;
    this.replayFrozenInputRun=report.savedRunId;
    this.replayBaselineBuild=report.baselineBuildId??'';
    this.normalReplayTrial='fixed-target';
    this.runNormalReplay();
  }
  protected runNormalReplay(gapAware = false):void {
    if(this.normalReplayBusy || this.gapAwareBusy)return;
    if((gapAware && (!this.replayTo || !!this.replayFrozenInputRun)) ||
        (!gapAware && !this.replayFrozenInputRun && (!this.replayFrom || !this.replayTo || this.replayFrom>this.replayTo)) ||
        (this.normalReplayTrial==='custom-stop' && (this.normalReplayStopPercent<=0 || this.normalReplayStopPercent>75))) {
      this.normalReplayError='Choose completed dates and a premium stop between 0 and 75%.';return;
    }
    const {replayFrom:from,replayTo:to,normalReplayStopPercent:stop,replayBaselineBuild:baseline,
      replayProposalBuild:proposal,replayFrozenInputRun:dataset,normalReplayTrial:trial}=this;
    const market=gapAware?this.gapAwareMarket:null;
    this.submitNormalJob(['compare',gapAware?to:from,to,stop,baseline,proposal,dataset,trial,market], id =>
      this.historicalReplayService.compareNormal(gapAware?to:from,to,stop,baseline,proposal,dataset,trial,id,market));
  }
  private readJobKey(key:string):string|null {
    try{return localStorage.getItem(key);}catch{return null;}
  }
  private writeJobKey(key:string,value:string|null):void {
    try{if(value)localStorage.setItem(key,value);else localStorage.removeItem(key);}catch{/* Watching still works in memory. */}
  }
  private resumeNormalJob():void {
    const previous=this.readJobKey(this.normalReplayJobKey);
    const pending=this.readJobKey(this.normalReplayRequestKey)?.slice(0,36);
    if(!pending){if(previous)this.watchNormalJob(previous);return;}
    this.normalReplayBusy=true;
    this.normalReplayStatusText='Looking for the saved comparison…';
    this.subscriptions.add(this.historicalReplayService.gapAwareStatus(pending).subscribe({
      next:job=>{
        this.writeJobKey(this.normalReplayJobKey,job.id);
        this.writeJobKey(this.normalReplayRequestKey,null);
        this.watchNormalJob(job.id,job);
        this.refreshView();
      },
      error:()=>{
        this.normalReplayBusy=false;
        this.normalReplayError='The last request was not confirmed. Retry the same comparison to reconnect.';
        if(previous)this.watchNormalJob(previous);
        this.refreshView();
      },
    }));
  }
  private submitNormalJob(identity:unknown[],submit:(id:string)=>ReturnType<HistoricalReplayService['comparePaperSession']>):void {
    const signature=JSON.stringify(identity);
    const existing=this.readJobKey(this.normalReplayRequestKey);
    const requestId=existing?.slice(37)===signature ? existing.slice(0,36) : crypto.randomUUID();
    this.writeJobKey(this.normalReplayRequestKey,requestId+':'+signature);
    this.normalReplayBusy=true;this.normalReplayError='';this.normalReplayReport=null;this.gapComparisonReport=null;
    this.normalReplayStatusText='Saving replay request…';
    this.subscriptions.add(submit(requestId).subscribe({
      next:job=>{
        this.writeJobKey(this.normalReplayJobKey,job.id);
        this.writeJobKey(this.normalReplayRequestKey,null);
        this.watchNormalJob(job.id,job);
      },
      error:error=>{
        if(error.status>=400 && error.status<500 && error.status!==429)this.writeJobKey(this.normalReplayRequestKey,null);
        this.normalReplayBusy=false;
        this.normalReplayError=typeof error.error==='string'?error.error:'The request was not confirmed. Retry the same comparison to reconnect.';
        this.refreshView();
      },
    }));
  }
  private watchNormalJob(id:string,initial?:GapReplayJob):void {
    this.normalReplayBusy=true;
    this.normalReplayStatusText=initial ? this.normalJobStatus(initial) : 'Reconnecting to saved comparison…';
    const watch=timer(0,5000).pipe(exhaustMap(()=>this.historicalReplayService.gapAwareStatus(id).pipe(
      catchError(e=>of(e.status===404?'missing' as const:e.status===401||e.status===403?'sign-in' as const:null))
    ))).subscribe(job=>{
      if(job==='missing'||job==='sign-in'){
        if(job==='missing')this.writeJobKey(this.normalReplayJobKey,null);
        this.normalReplayError=job==='missing'?'Saved comparison is no longer available.':'Sign in and reopen Historical Replay to resume watching.';
        this.normalReplayBusy=false;watch.unsubscribe();
      }else if(!job)this.normalReplayStatusText='Connection interrupted. Reconnecting to the saved comparison…';
      else if(job.status==='Completed'||job.status==='Failed'){
        this.normalReplayReport=job.comparisonReport??null;
        this.gapComparisonReport=job.gapComparisonReport??null;
        this.normalReplayError=job.error??'';
        this.normalReplayBusy=false;watch.unsubscribe();
        if(this.normalReplayReport)this.loadSavedNormalRuns();
      }else this.normalReplayStatusText=this.normalJobStatus(job);
      this.refreshView();
    });
    this.subscriptions.add(watch);
  }
  private normalJobStatus(job:GapReplayJob):string {
    const dates=job.from && job.from!==job.date ? `${job.from} → ${job.date}` : job.date;
    return `${dates} · ${job.phase||job.status}. You can close this panel and come back.`;
  }
  protected checkReplayCohort():void {
    if (!this.replayFrom || !this.replayTo || this.replayFrom>this.replayTo) {
      this.replayCohortError='Select a valid completed date range.'; return;
    }
    this.replayCohortBusy=true; this.replayCohortError=''; this.replayCohort=null;
    this.subscriptions.add(this.historicalReplayService.cohortCoverage(
      this.replayFrom,this.replayTo).subscribe({
      next:value=>{this.replayCohort=value;this.replayCohortBusy=false;this.refreshView();},
      error:error=>{this.replayCohortBusy=false;this.replayCohortError=
        error?.name==='TimeoutError'?'Session eligibility check exceeded three minutes. No eligibility verdict was produced; retry a shorter date range.':
        typeof error?.error==='string'?error.error:'Full-session coverage unavailable for this date range.';this.refreshView();},
    }));
  }
  protected runHistoricalReplay():void {
    if (!this.replayFrom || !this.replayTo || this.replayFrom>this.replayTo) {
      this.replayError='Select a valid completed date range.'; return;
    }
    this.replayLoading=true; this.replayError=''; this.replayReport=null;
    this.subscriptions.add(this.historicalReplayService.get(this.replayFrom,this.replayTo,
      this.replayMarket,this.replayStopPercent,this.replayTargetPercent).subscribe({
      next:value=>{this.replayReport=value;this.replayLoading=false;this.refreshView();},
      error:()=>{this.replayLoading=false;this.replayError='Replay unavailable. Check dates, parameters and archive coverage.';this.refreshView();},
    }));
  }
  protected backfillReplayDate():void {
    this.replayBackfillBusy=true; this.replayBackfillMessage='';
    this.subscriptions.add(this.historicalReplayService.backfill(this.replayTo,
      this.replayMarket).subscribe({
      next:value=>{
        this.replayBackfillBusy=false;
        this.replayBackfillMessage=`Imported ${value.addedCandles} option bars from ${value.fetchedContracts} recorded contracts.`+
          (value.unavailableSymbols.length ? ` ${value.unavailableSymbols.length} contracts lacked broker metadata.` : '');
        this.runHistoricalReplay(); this.refreshView();
      },
      error:()=>{this.replayBackfillBusy=false;this.replayBackfillMessage='Backfill unavailable. Use a completed day outside market hours with a valid Groww connection.';this.refreshView();},
    }));
  }
  protected backfillReplayIndex():void {
    this.replayIndexBusy=true; this.replayIndexMessage='';
    this.subscriptions.add(this.historicalReplayService.backfillIndex(this.replayTo,
      this.replayMarket).subscribe({
      next:value=>{
        this.replayIndexBusy=false;
        this.replayIndexMessage=`Stored ${value.addedCandles} new index candles; ${value.presentMinutes}/${value.expectedMinutes} session minutes covered.`;
        this.runHistoricalReplay(); this.refreshView();
      },
      error:()=>{
        this.replayIndexBusy=false;
        this.replayIndexMessage='Index import unavailable. Use a completed session outside market hours with a valid Groww connection.';
        this.refreshView();
      },
    }));
  }
  protected backfillReplayFuture():void {
    this.replayFutureBusy=true; this.replayFutureMessage='';
    this.subscriptions.add(this.historicalReplayService.backfillFuture(this.replayTo,
      this.replayMarket).subscribe({
      next:value=>{
        this.replayFutureBusy=false;
        this.replayFutureMessage=`Stored ${value.addedCandles} new front-future candles for ${value.growwSymbol}${value.matchedInstrumentMetadata?'':' in the research archive'}.`;
        this.refreshView();
      },
      error:()=>{
        this.replayFutureBusy=false;
        this.replayFutureMessage='No verified historical future was available or import failed. Check the completed day and Groww connection.';
        this.refreshView();
      },
    }));
  }
  protected checkReplayArchiveCoverage():void {
    this.replayCoverageBusy=true; this.replayCoverageMessage='';
    this.subscriptions.add(this.historicalReplayService.archiveCoverage(this.replayTo,
      this.replayMarket).subscribe({
      next:value=>{
        this.replayCoverageBusy=false;
        this.replayCoverageMessage=`Index ${value.indexPresentMinutes}/375 · futures ${value.frontFuturePresentMinutes}/375 · prior close ${value.previousCloseAvailable?'ready':'missing'} · mapped option paths ${value.mappedContractsWithPricePath}/${value.mappedOptionContracts} · unmapped paths ${value.unmappedContractsWithPricePath} · selected expiry pages ${value.completeSelectedExpiryUniverses}/${value.recordedExpiryUniverses} complete · selected contract paths ${value.selectedPageContractsWithPricePath}/${value.selectedPageContracts} (full minute paths ${value.selectedPageContractsWithCompleteMinutePath}; matched lot metadata ${value.selectedPageContractsWithMappedLotMetadata}) · dated pre-open master ${value.datedInstrumentMasterFingerprint?'captured':'missing'} · executable bid/offer ${value.executableBidOfferObservations}/${value.optionQuoteObservations} · chain receipt LTP ${value.chainLtpObservations} (OI ${value.chainLtpObservationsWithOi}) · candle-close proxies ${value.candleCloseProxyObservations} · chain snapshots ${value.receivedOptionChainSnapshots}. ${value.underlyingReady?'Underlying archive complete; full strategy inputs and executable option universe still unverified.':'Underlying archive incomplete.'}`;
        this.refreshView();
      },
      error:()=>{
        this.replayCoverageBusy=false;
        this.replayCoverageMessage='Archive coverage unavailable for this completed day.';
        this.refreshView();
      },
    }));
  }
  protected discoverReplayExpiries():void {
    this.replayDiscoveryBusy=true; this.replayUniverseMessage='';
    this.replayExpiries=[]; this.replayExpiry=''; this.replayUniversePage=0;
    this.subscriptions.add(this.historicalReplayService.expiries(this.replayTo,
      this.replayMarket).subscribe({
      next:value=>{
        this.replayDiscoveryBusy=false;
        this.replayExpiries=value.expiries;
        this.replayExpiry=value.expiries[0] ?? '';
        this.replayUniverseMessage=value.expiries.length
          ? `${value.expiries.length} historical expiries found. Select an expiry and import pages.`
          : 'No historical expiry found for this completed day.';
        this.refreshView();
      },
      error:()=>{this.replayDiscoveryBusy=false;
        this.replayUniverseMessage='Expiry discovery unavailable. Try outside market hours with a valid Groww connection.';
        this.refreshView();},
    }));
  }
  protected backfillReplayUniversePage():void {
    if (!this.replayExpiry || !this.replayExpiries.includes(this.replayExpiry) ||
        !Number.isInteger(this.replayUniversePage) || this.replayUniversePage<0) {
      this.replayUniverseMessage='Discover and select a valid expiry and page first.'; return;
    }
    this.replayUniverseBusy=true; this.replayUniverseMessage='';
    this.subscriptions.add(this.historicalReplayService.backfillUniverse(this.replayTo,
      this.replayMarket,this.replayExpiry,this.replayUniversePage).subscribe({
      next:value=>{
        this.replayUniverseBusy=false;
        this.replayUniverseMessage=`Page ${value.page + 1}: imported ${value.addedCandles} bars from ${value.fetchedContracts} of ${value.totalContractsInCorridor} corridor contracts.`+
          (value.missingInstrumentMetadata.length
            ? ` ${value.archivedUnmappedCandles} more bars were archived for ${value.missingInstrumentMetadata.length} contracts without lot metadata; they cannot yet be priced as trades.` : '');
        this.runHistoricalReplay(); this.refreshView();
      },
      error:()=>{this.replayUniverseBusy=false;
        this.replayUniverseMessage='Universe import failed. Check the selected completed day, expiry and page; it only runs outside market hours.';
        this.refreshView();},
    }));
  }

}
