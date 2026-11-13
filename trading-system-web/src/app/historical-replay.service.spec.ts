import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { HistoricalReplayService, replayProposal } from './historical-replay.service';
import { TestScheduler } from 'rxjs/testing';

describe('three-market replay experiments',()=>{
  const base={niftyAdmission:{stopLossPercent:10},sensexEntry:{stopPercent:12},bankNiftyStopPercent:8,
    niftyExits:{protectProfitableConfirmedReversals:true,reversalProfitLockFraction:.8,targetExtensionEnabled:true},
    marketExits:{protectProfitableReversals:true,reversalLockFraction:.8,targetExtensionEnabled:true}};
  it('isolates each proposal from baseline and other settings',()=>{
    expect(replayProposal(base,'control',4)).toEqual(base);
    expect(replayProposal(base,'smaller-stop',4)).toEqual({...base,niftyAdmission:{stopLossPercent:8},
      sensexEntry:{stopPercent:9.6},bankNiftyStopPercent:6.4});
    expect(replayProposal(base,'reversal-lock',4)).toEqual({...base,
      niftyExits:{...base.niftyExits,reversalProfitLockFraction:.9},marketExits:{...base.marketExits,reversalLockFraction:.9}});
    expect(replayProposal(base,'fixed-target',4)).toEqual({...base,
      niftyExits:{...base.niftyExits,targetExtensionEnabled:false},marketExits:{...base.marketExits,targetExtensionEnabled:false}});
    expect(base.niftyExits.targetExtensionEnabled).toBe(true);
  });
});

describe('HistoricalReplayService', () => {
  beforeEach(() => TestBed.configureTestingModule({
    providers: [provideHttpClient(), provideHttpClientTesting()],
  }));

  it('submits a protected gap-aware job and retrieves it through a separate status request', () => {
    const service=TestBed.inject(HistoricalReplayService);
    const http=TestBed.inject(HttpTestingController);
    let id='';
    service.gapAware('2026-11-12','bank-nifty').subscribe(job=>id=job.id);
    http.expectOne('/api/security/antiforgery-token').flush({token:'csrf'});
    const request=http.expectOne(req=>req.url.endsWith('/gap-aware/2026-11-12'));
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('X-CSRF-TOKEN')).toBe('csrf');
    expect(request.request.params.get('market')).toBe('bank-nifty');
    request.flush({id:'saved-job',status:'Queued'},{status:202,statusText:'Accepted'});
    let status='';
    service.gapAwareStatus(id).subscribe(job=>status=job.status);
    const saved=http.expectOne('/api/research/historical-replay/normal-policy/gap-aware/jobs/saved-job');
    expect(saved.request.method).toBe('GET');
    saved.flush({id,status:'Completed',report:{date:'2026-11-12',markets:[]}});
    expect(status).toBe('Completed');
    http.verify();
  });

  it('reuses the request ID when a saved replay is not acknowledged before the timeout', () => {
    const service=TestBed.inject(HistoricalReplayService);
    const http=TestBed.inject(HttpTestingController);
    const id='743aae12-5287-4d14-903e-b9b2fada6aa6';
    let result='';
    vi.useFakeTimers();
    try {
      service.gapAware('2026-11-12','bank-nifty',id).subscribe(job=>result=job.id);
      http.expectOne('/api/security/antiforgery-token').flush({token:'csrf'});
      const first=http.expectOne(req=>req.url.endsWith('/gap-aware/2026-11-12'));
      expect(first.request.params.get('requestId')).toBe(id);
      vi.advanceTimersByTime(32000);
      expect(first.cancelled).toBe(true);
      http.expectOne('/api/security/antiforgery-token').flush({token:'csrf'});
      const retry=http.expectOne(req=>req.url.endsWith('/gap-aware/2026-11-12'));
      expect(retry.request.params.get('requestId')).toBe(id);
      retry.flush({id,status:'Completed'},{status:202,statusText:'Accepted'});
      expect(result).toBe(id);
      http.verify();
    } finally { vi.useRealTimers(); }
  });

  it('reads archive coverage for the selected completed market day', () => {
    const service = TestBed.inject(HistoricalReplayService);
    const http = TestBed.inject(HttpTestingController);
    service.archiveCoverage('2026-10-29', 'nifty').subscribe();
    const request = http.expectOne(value => value.url ===
      '/api/research/historical-replay/archive-coverage');
    expect(request.request.method).toBe('GET');
    expect(request.request.params.get('date')).toBe('2026-10-29');
    expect(request.request.params.get('market')).toBe('nifty');
    request.flush({date:'2026-10-29',market:'nifty',underlyingReady:false});
    http.verify();
  });

  it('freezes baseline settings and submits a separate protected normal-policy proposal', () => {
    const service=TestBed.inject(HistoricalReplayService);
    const http=TestBed.inject(HttpTestingController);
    const baseline={niftyAdmission:{stopLossPercent:10,maximumPremium:500},
      sensexEntry:{stopPercent:10,lots:5},bankNiftyStopPercent:10,frozenMarker:'unchanged'};
    service.compareNormal('2026-11-02','2026-11-03',8,'old-approved','new-approved','','custom-stop','compare-1').subscribe();
    http.expectOne('/api/research/historical-replay/normal-policy/configuration').flush(baseline);
    http.expectOne('/api/security/antiforgery-token').flush({token:'csrf'});
    const request=http.expectOne(value=>value.url==='/api/research/historical-replay/normal-policy/compare');
    expect(request.request.method).toBe('POST');
    expect(request.request.body.baselineBuildId).toBe('old-approved');
    expect(request.request.body.proposalBuildId).toBe('new-approved');
    expect(request.request.headers.get('X-CSRF-TOKEN')).toBe('csrf');
    expect(request.request.params.get('requestId')).toBe('compare-1');
    expect(request.request.body.baseline.niftyAdmission.stopLossPercent).toBe(10);
    expect(request.request.body.proposal.niftyAdmission.stopLossPercent).toBe(8);
    expect(request.request.body.proposal.sensexEntry.stopPercent).toBe(8);
    expect(request.request.body.proposal.bankNiftyStopPercent).toBe(8);
    expect(request.request.body.proposal.frozenMarker).toBe('unchanged');
    expect(request.request.body.execution.minimumMinutesBetweenEntries).toBe(0);
    expect(baseline.niftyAdmission.stopLossPercent).toBe(10);
    request.flush({id:'compare-1',status:'Queued'});
    http.verify();
  });

  it('reads full-session cohort exclusions for a selected date range', () => {
    const service = TestBed.inject(HistoricalReplayService);
    const http = TestBed.inject(HttpTestingController);
    service.cohortCoverage('2026-11-02', '2026-11-03').subscribe();
    const request = http.expectOne(value => value.url ===
      '/api/research/historical-replay/cohort-coverage');
    expect(request.request.method).toBe('GET');
    expect(request.request.params.get('from')).toBe('2026-11-02');
    expect(request.request.params.get('to')).toBe('2026-11-03');
    expect(request.request.params.get('allowModeledHistoricalCosts')).toBe('true');
    request.flush({from:'2026-11-02',to:'2026-11-03',includedDates:[],
      excludedDates:[{date:'2026-11-02',reasons:['NIFTY: archive missing']}]});
    http.verify();
  });

  it('cancels a stalled eligibility request instead of checking indefinitely', () => {
    const service=TestBed.inject(HistoricalReplayService);
    const http=TestBed.inject(HttpTestingController);
    new TestScheduler(()=>{}).run(({flush})=>{
      let failure='';
      service.cohortCoverage('2026-11-09','2026-11-09').subscribe({error:error=>failure=error.name});
      const request=http.expectOne(value=>value.url==='/api/research/historical-replay/cohort-coverage');
      flush();
      expect(failure).toBe('TimeoutError');
      expect(request.cancelled).toBe(true);
    });
    http.verify();
  });

  it('reuses frozen dates, settings and assumptions instead of querying current controls', () => {
    const service=TestBed.inject(HistoricalReplayService);
    const http=TestBed.inject(HttpTestingController);
    service.compareNormal('2026-11-01','2026-11-05',7,'','','saved-run').subscribe();
    http.expectOne('/api/research/historical-replay/normal-policy/runs/saved-run/configuration').flush({
      from:'2026-11-02',to:'2026-11-03',baselineBuildId:'original-build',
      baseline:{niftyAdmission:{stopLossPercent:10},sensexEntry:{stopPercent:10},bankNiftyStopPercent:10},
      execution:{frozen:'cost-model'},
    });
    http.expectNone('/api/research/historical-replay/normal-policy/configuration');
    http.expectOne('/api/security/antiforgery-token').flush({token:'csrf'});
    const request=http.expectOne(value=>value.url==='/api/research/historical-replay/normal-policy/compare');
    expect(request.request.body.from).toBe('2026-11-02');
    expect(request.request.body.to).toBe('2026-11-03');
    expect(request.request.body.execution).toEqual({frozen:'cost-model'});
    expect(request.request.body.baselineBuildId).toBe('original-build');
    expect(request.request.body.proposalBuildId).toBeNull();
    expect(request.request.body.frozenInputRunId).toBe('saved-run');
    expect(request.request.body.proposal.niftyAdmission.stopLossPercent).toBe(7);
    request.flush({id:'compare-2',status:'Queued'});
    http.verify();
  });

  it('queues paper verification and retrieves its persisted result separately', () => {
    const service=TestBed.inject(HistoricalReplayService);
    const http=TestBed.inject(HttpTestingController);
    service.comparePaperSession('2026-11-13','paper-1').subscribe(job=>{
      service.gapAwareStatus(job.id).subscribe(result=>expect(result.comparisonReport?.limitations).toEqual(['Excluded']));
    });
    http.expectOne('/api/security/antiforgery-token').flush({token:'csrf'});
    const submit=http.expectOne(request=>request.url.endsWith('/paper-session/2026-11-13'));
    expect(submit.request.params.get('requestId')).toBe('paper-1');
    expect(submit.request.headers.get('X-CSRF-TOKEN')).toBe('csrf');
    submit.flush({id:'paper-1',status:'Queued'});
    http.expectOne('/api/research/historical-replay/normal-policy/gap-aware/jobs/paper-1')
      .flush({id:'paper-1',status:'Completed',comparisonReport:{limitations:['Excluded']}});
    http.verify();
  });

  it('imports an index day only through an antiforgery-protected request', () => {
    const service = TestBed.inject(HistoricalReplayService);
    const http = TestBed.inject(HttpTestingController);
    service.backfillIndex('2026-10-29', 'sensex').subscribe();
    http.expectOne('/api/security/antiforgery-token').flush({token:'csrf'});
    const request = http.expectOne(
      '/api/research/historical-replay/backfill-index-session');
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('X-CSRF-TOKEN')).toBe('csrf');
    expect(request.request.body).toEqual({date:'2026-10-29',market:'sensex'});
    request.flush({date:'2026-10-29',market:'sensex',addedCandles:375,
      presentMinutes:375,expectedMinutes:375,missingMinutes:0});
    http.verify();
  });

  it('imports a verified historical future through an antiforgery-protected request', () => {
    const service = TestBed.inject(HistoricalReplayService);
    const http = TestBed.inject(HttpTestingController);
    service.backfillFuture('2026-10-29', 'bank-nifty').subscribe();
    http.expectOne('/api/security/antiforgery-token').flush({token:'csrf'});
    const request = http.expectOne(
      '/api/research/historical-replay/backfill-front-future');
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('X-CSRF-TOKEN')).toBe('csrf');
    expect(request.request.body).toEqual({date:'2026-10-29',market:'bank-nifty'});
    request.flush({date:'2026-10-29',market:'bank-nifty',
      expiryDate:'2026-11-26',growwSymbol:'NSE-BANKNIFTY-26Nov26-FUT',
      matchedInstrumentMetadata:false,addedCandles:375});
    http.verify();
  });
});
