import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable, switchMap } from 'rxjs';

export interface HistoricalReplayFill {
  symbol:string; strategy:string; decisionAtUtc:string; entryAtUtc:string; exitAtUtc:string;
  entryPrice:number; exitPrice:number; quantity:number; grossPnl:number; costs:number;
  netPnl:number; exitReason:string;
  initialStopPrice?:number|null; finalStopPrice?:number|null;
  initialTargetPrice?:number|null; finalTargetPrice?:number|null;
}
export interface HistoricalReplaySessionCoverage {
  date:string; indexCandles:number; optionChainSnapshots:number; optionCandles:number; recordedCandidates:number;
  baselineTrades:number; baselineNetPnl:number; status:string; expectedIndexMinutes:number;
  missingIndexMinutes:number; longestIndexGapMinutes:number;
}
export interface HistoricalReplayResearchReport {
  from:string; to:string; market:string; variant:string; dataFingerprint:string;
  parameters:{stopPercent:number;targetPercent:number;entrySlippagePercent:number;
    exitSlippagePercent:number;maximumHoldMinutes:number;minimumMinutesBetweenEntries:number};
  sessions:HistoricalReplaySessionCoverage[];
  validation:{trainingDays:number;validationDays:number;trainingTrades:number;validationTrades:number;
    trainingNetPnl:number;validationNetPnl:number;verdict:string};
  simulation:{trades:HistoricalReplayFill[];candidateCount:number;missingEntryQuoteCount:number;
    missingExitQuoteCount:number;staleQuoteCount:number;netPnl:number;maximumDrawdown:number;
    wins:number;winRate:number;expectancy:number;totalCosts:number;profitFactor:number;
    executionAssumption:string};
  referenceSimulation:{trades:HistoricalReplayFill[];candidateCount:number;missingEntryQuoteCount:number;
    missingExitQuoteCount:number;staleQuoteCount:number;netPnl:number;maximumDrawdown:number;
    wins:number;winRate:number;expectancy:number;totalCosts:number;profitFactor:number;
    executionAssumption:string};
  dayComparisons:{date:string;referenceNetPnl:number;proposedNetPnl:number;difference:number;
    referenceTrades:number;proposedTrades:number}[];
  baselineNetPnl:number; limitations:string[];
}
export interface HistoricalReplayArchiveCoverage {
  date:string; market:string; indexPresentMinutes:number; indexMissingMinutes:number;
  frontFuturePresentMinutes:number; frontFutureMissingMinutes:number;
  previousCloseAvailable:boolean; mappedOptionContracts:number;
  mappedContractsWithPricePath:number; unmappedContractsWithPricePath:number;
  receivedOptionChainSnapshots:number; optionQuoteObservations:number;
  optionQuoteObservationsWithOi:number;
  recordedExpiryUniverses:number; completeSelectedExpiryUniverses:number;
  selectedPageContracts:number; selectedPageContractsWithPricePath:number;
  selectedPageContractsWithCompleteMinutePath:number;
  selectedPageContractsWithMappedLotMetadata:number;
  datedInstrumentMasterCapturedAtUtc:string|null;
  datedInstrumentMasterFingerprint:string|null;
  executableBidOfferObservations:number;
  chainLtpObservations:number;
  candleCloseProxyObservations:number;
  chainLtpObservationsWithOi:number;
  underlyingReady:boolean;
}
export interface HistoricalReplayCohortCoverage {
  from:string; to:string; includedDates:string[];
  excludedDates:{date:string;reasons:string[]}[];
}
export interface HistoricalNormalPolicyConfiguration {
  niftyAdmission:{stopLossPercent:number;[key:string]:unknown};
  sensexEntry:{stopPercent:number;[key:string]:unknown};
  bankNiftyStopPercent:number;
  [key:string]:unknown;
}
export type ReplayTrial = 'control'|'custom-stop'|'smaller-stop'|'reversal-lock'|'fixed-target';

// Only the proposal is edited. No live settings API is involved.
export function replayProposal(base:HistoricalNormalPolicyConfiguration,trial:ReplayTrial,customStop:number) {
  const p=structuredClone(base);
  if(trial==='control') return p;
  if(trial==='custom-stop' || trial==='smaller-stop') {
    const distance=(n:number)=>trial==='custom-stop'?customStop:Number((n*0.8).toFixed(4));
    p.niftyAdmission.stopLossPercent=distance(base.niftyAdmission.stopLossPercent);
    p.sensexEntry.stopPercent=distance(base.sensexEntry.stopPercent);
    p.bankNiftyStopPercent=distance(base.bankNiftyStopPercent);
  } else {
    const nifty=p['niftyExits'] as Record<string,unknown>, other=p['marketExits'] as Record<string,unknown>;
    if(!nifty || !other) throw new Error('Frozen settings do not support this exit experiment.');
    if(trial==='reversal-lock') {
      if(nifty['protectProfitableConfirmedReversals']!==true || other['protectProfitableReversals']!==true)
        throw new Error('This experiment requires existing confirmed-reversal protection for all three markets.');
      nifty['reversalProfitLockFraction']=0.9; other['reversalLockFraction']=0.9;
    } else if(trial==='fixed-target') {
      nifty['targetExtensionEnabled']=false; other['targetExtensionEnabled']=false;
    } else throw new Error('Unknown replay trial.');
  }
  if(trial!=='custom-stop' && JSON.stringify(p)===JSON.stringify(base))
    throw new Error('This proposal already matches the frozen baseline. No experiment was run.');
  return p;
}
export interface HistoricalNormalReplayReport {
  baselineBuildId:string|null;proposalBuildId:string|null;
  savedRunId:string|null;executionBuildId:string|null;
  comparison:{
    cohort:{includedDates:string[];excludedDates:{date:string;reasons:string[]}[]};
    days:{date:string;netDifference:number;baseline:{netPnl:number};proposal:{netPnl:number}}[];
    baselineNet:number;proposalNet:number;baselineCosts:number;proposalCosts:number;
    baselineTrades:number;proposalTrades:number;baselineWinRate:number;proposalWinRate:number;
    baselineExpectancy:number;proposalExpectancy:number;
    baselineSampledGiveback:number|null;proposalSampledGiveback:number|null;
    baselineWorstSessionDrawdown:number;proposalWorstSessionDrawdown:number;
    trainingDays:number;validationDays:number;trainingNetDifference:number;validationNetDifference:number;
    dataFingerprint:string;runFingerprint:string;evidenceStatus:string;evidenceGaps:string[];
  };
  limitations:string[];
}
export interface HistoricalPolicyBuild {id:string;capturedAtUtc:string;assemblyHashes:Record<string,string>}
export interface HistoricalNormalReplayRunSummary {
  id:string;createdAtUtc:string;from:string;to:string;baselineBuildId:string|null;proposalBuildId:string|null;
  includedDays:number;baselineNet:number;proposalNet:number;
}
interface NormalReplaySavedConfiguration {
  from:string;to:string;baseline:HistoricalNormalPolicyConfiguration;
  baselineBuildId:string|null;execution:Record<string,unknown>;
}
@Injectable({providedIn:'root'})
export class HistoricalReplayService {
  private readonly http=inject(HttpClient);
  policyBuilds() {
    return this.http.get<HistoricalPolicyBuild[]>('/api/research/historical-replay/normal-policy/builds');
  }
  savedNormalRuns() {
    return this.http.get<HistoricalNormalReplayRunSummary[]>('/api/research/historical-replay/normal-policy/runs');
  }
  savedNormalReport(id:string) {
    return this.http.get<HistoricalNormalReplayReport>('/api/research/historical-replay/normal-policy/runs/'+encodeURIComponent(id));
  }
  compareNormal(from:string,to:string,stopPercent:number,baselineBuildId='',proposalBuildId='',frozenInputRunId='',trial:ReplayTrial='custom-stop') {
    const configuration:Observable<HistoricalNormalPolicyConfiguration|NormalReplaySavedConfiguration>=frozenInputRunId
      ? this.http.get<NormalReplaySavedConfiguration>('/api/research/historical-replay/normal-policy/runs/'+
          encodeURIComponent(frozenInputRunId)+'/configuration')
      : this.http.get<HistoricalNormalPolicyConfiguration>('/api/research/historical-replay/normal-policy/configuration');
    return configuration.pipe(
      switchMap(value=>{
        const saved=frozenInputRunId?value as NormalReplaySavedConfiguration:null;
        const baseline=saved?saved.baseline:value as HistoricalNormalPolicyConfiguration;
        const proposal=replayProposal(baseline,trial,stopPercent);
        return this.http.get<{token:string}>('/api/security/antiforgery-token').pipe(
          switchMap(csrf=>this.http.post<HistoricalNormalReplayReport>(
            '/api/research/historical-replay/normal-policy/compare',{
              from:saved?.from??from,to:saved?.to??to,baseline,proposal,
              baselineBuildId:baselineBuildId||saved?.baselineBuildId||null,
              proposalBuildId:trial==='custom-stop' ? proposalBuildId||null : baselineBuildId||saved?.baselineBuildId||null,
              frozenInputRunId:frozenInputRunId||null,
              execution:saved?.execution??{stopPercent:.1,targetPercent:.1,
                entrySlippagePercent:.0025,exitSlippagePercent:.0025,
                maximumHoldMinutes:375,minimumMinutesBetweenEntries:0,maximumConcurrentPositions:10,
                allowModeledHistoricalCosts:true}
            },{headers:{'X-CSRF-TOKEN':csrf.token}})));
      }));
  }
  get(from:string,to:string,market:string,stopPercent:number,targetPercent:number) {
    const params=new HttpParams().set('from',from).set('to',to).set('market',market)
      .set('stopPercent',stopPercent).set('targetPercent',targetPercent);
    return this.http.get<HistoricalReplayResearchReport>('/api/research/historical-replay',{params});
  }
  archiveCoverage(date:string,market:string) {
    return this.http.get<HistoricalReplayArchiveCoverage>(
      '/api/research/historical-replay/archive-coverage',
      {params:new HttpParams().set('date',date).set('market',market)});
  }
  cohortCoverage(from:string,to:string) {
    return this.http.get<HistoricalReplayCohortCoverage>(
      '/api/research/historical-replay/cohort-coverage',
      {params:new HttpParams().set('from',from).set('to',to)});
  }
  backfill(date:string,market:string) {
    return this.http.get<{token:string}>('/api/security/antiforgery-token').pipe(
      switchMap(value=>this.http.post<{date:string;market:string;selectedContracts:number;
        fetchedContracts:number;addedCandles:number;unavailableSymbols:string[]}>(
        '/api/research/historical-replay/backfill-recorded-candidates',
        {date,market,maximumContracts:20},
        {headers:{'X-CSRF-TOKEN':value.token}})));
  }
  backfillIndex(date:string,market:string) {
    return this.http.get<{token:string}>('/api/security/antiforgery-token').pipe(
      switchMap(value=>this.http.post<{date:string;market:string;addedCandles:number;
        presentMinutes:number;expectedMinutes:number;missingMinutes:number}>(
        '/api/research/historical-replay/backfill-index-session',
        {date,market},{headers:{'X-CSRF-TOKEN':value.token}})));
  }
  backfillFuture(date:string,market:string) {
    return this.http.get<{token:string}>('/api/security/antiforgery-token').pipe(
      switchMap(value=>this.http.post<{date:string;market:string;expiryDate:string;
        growwSymbol:string;matchedInstrumentMetadata:boolean;addedCandles:number}>(
        '/api/research/historical-replay/backfill-front-future',
        {date,market},{headers:{'X-CSRF-TOKEN':value.token}})));
  }
  expiries(date:string,market:string) {
    return this.http.get<{date:string;market:string;expiries:string[]}>(
      '/api/research/historical-replay/expiries',
      {params:new HttpParams().set('date',date).set('market',market)});
  }
  backfillUniverse(date:string,market:string,expiryDate:string,page:number) {
    return this.http.get<{token:string}>('/api/security/antiforgery-token').pipe(
      switchMap(value=>this.http.post<{date:string;market:string;expiryDate:string;
        totalContractsInCorridor:number;page:number;selectedOnPage:number;
        fetchedContracts:number;addedCandles:number;archivedUnmappedCandles:number;
        missingInstrumentMetadata:string[]}>(
        '/api/research/historical-replay/backfill-universe-page',
        {date,market,expiryDate,page,pageSize:20},
        {headers:{'X-CSRF-TOKEN':value.token}})));
  }
}
