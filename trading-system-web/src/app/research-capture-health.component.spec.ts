import {provideHttpClient} from '@angular/common/http';
import {HttpTestingController, provideHttpClientTesting} from '@angular/common/http/testing';
import {TestBed} from '@angular/core/testing';
import {ResearchCaptureHealthComponent} from './research-capture-health.component';

describe('automatic replay capture warning',()=>{
  beforeEach(()=>{
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-11-06T04:00:00Z'));
    TestBed.configureTestingModule({imports:[ResearchCaptureHealthComponent],
      providers:[provideHttpClient(),provideHttpClientTesting()]});
  });
  afterEach(()=>{TestBed.resetTestingModule();vi.useRealTimers();});
  const state=(expectedNow:boolean,issues:string[])=>({
    date:'2026-11-06',checkedAt:'2026-11-06T04:00:00Z',master:null,preOpenMaster:false,
    expectedNow,issues,feed:{phase:issues.length?'Interrupted':'Subscribed',instruments:0,subscribedAtUtc:null,
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
    expect(warning.querySelector('summary').textContent.trim()).toBe('Replay · Feed warning');
    expect(warning.open).toBe(false);
    expect(warning.querySelector('.capture-warning__body').textContent).toContain('09:30:00 IST');
    expect(warning.querySelector('li').textContent).toBe('Feed authorization failed');
    warning.open=true;
    warning.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}));
    fixture.detectChanges();
    expect(warning.open).toBe(false);
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
    expect(fixture.nativeElement.querySelector('.capture-warning--idle')).not.toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Outside capture hours');
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
    expect(fixture.nativeElement.querySelector('.capture-warning--healthy')).not.toBeNull();
    fixture.destroy();http.verify();
  });
  it('updates green to red to green, and never flashes green while retrying an unavailable check',async()=>{
    const fixture=TestBed.createComponent(ResearchCaptureHealthComponent);
    fixture.componentRef.setInput('compact',true);fixture.detectChanges();
    const http=TestBed.inject(HttpTestingController);
    const respond=(issues:string[])=>{
      http.expectOne('/api/research/capture-health').flush({...state(true,issues),checkedAt:new Date().toISOString()});
      fixture.detectChanges();
    };
    await vi.advanceTimersByTimeAsync(1);respond([]);
    expect(fixture.nativeElement.querySelector('.capture-warning--healthy')).not.toBeNull();
    await vi.advanceTimersByTimeAsync(30000);respond(['SENSEX option price/depth recording missing or stale']);
    expect(fixture.nativeElement.querySelector('.capture-warning--critical')).not.toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Recording failed');
    await vi.advanceTimersByTimeAsync(30000);respond([]);
    expect(fixture.nativeElement.querySelector('.capture-warning--healthy')).not.toBeNull();
    await vi.advanceTimersByTimeAsync(30000);
    http.expectOne('/api/research/capture-health').flush({}, {status:503,statusText:'Unavailable'});
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Status unavailable');
    expect(fixture.nativeElement.querySelector('.capture-warning--healthy')).toBeNull();
    await vi.advanceTimersByTimeAsync(30000);fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.capture-warning--healthy')).toBeNull();
    respond([]);
    expect(fixture.nativeElement.querySelector('.capture-warning--healthy')).not.toBeNull();
    fixture.destroy();http.verify();
  });
});
