import {provideHttpClient} from '@angular/common/http';
import {HttpTestingController, provideHttpClientTesting} from '@angular/common/http/testing';
import {TestBed} from '@angular/core/testing';
import {ResearchCaptureHealthComponent} from './research-capture-health.component';

describe('automatic replay capture warning',()=>{
  beforeEach(()=>{
    vi.useFakeTimers();
    TestBed.configureTestingModule({imports:[ResearchCaptureHealthComponent],
      providers:[provideHttpClient(),provideHttpClientTesting()]});
  });
  afterEach(()=>{TestBed.resetTestingModule();vi.useRealTimers();});
  const state=(expectedNow:boolean,issues:string[])=>({
    date:'2026-11-06',checkedAt:'2026-11-06T04:00:00Z',master:null,preOpenMaster:false,
    expectedNow,issues,feed:{phase:'Interrupted',instruments:0,subscribedAtUtc:null,
      gapObserved:true,lastReceiptByMarket:{},lastFailure:'Instrument plan: InvalidOperationException'}
  });
  it('automatically shows a specific recording failure and stops polling when destroyed',async()=>{
    const fixture=TestBed.createComponent(ResearchCaptureHealthComponent);
    fixture.componentRef.setInput('compact',true);fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(1);
    const http=TestBed.inject(HttpTestingController);
    http.expectOne('/api/research/capture-health').flush(state(true,['Feed authorization failed']));
    fixture.detectChanges();
    const warning=fixture.nativeElement.querySelector('details.capture-warning');
    expect(warning.querySelector('summary').textContent.trim()).toBe('Replay · Feed warning · 09:30:00');
    expect(warning.querySelector('li').textContent).toBe('Feed authorization failed');
    fixture.destroy();await vi.advanceTimersByTimeAsync(31000);
    http.expectNone('/api/research/capture-health');http.verify();
  });
  it('does not show an off-hours historical failure badge',async()=>{
    const fixture=TestBed.createComponent(ResearchCaptureHealthComponent);
    fixture.componentRef.setInput('compact',true);fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(1);
    TestBed.inject(HttpTestingController).expectOne('/api/research/capture-health')
      .flush(state(false,['Old recording interruption']));
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.capture-warning')).toBeNull();
    fixture.destroy();
  });
  it('retries after an HTTP failure instead of keeping a permanent spinner',async()=>{
    const fixture=TestBed.createComponent(ResearchCaptureHealthComponent);
    fixture.componentRef.setInput('compact',true);fixture.detectChanges();
    const http=TestBed.inject(HttpTestingController);
    await vi.advanceTimersByTimeAsync(1);
    http.expectOne('/api/research/capture-health').flush({}, {status:503,statusText:'Unavailable'});
    await vi.advanceTimersByTimeAsync(30000);
    http.expectOne('/api/research/capture-health').flush(state(true,[]));
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.capture-warning')).toBeNull();
    fixture.destroy();http.verify();
  });
});
