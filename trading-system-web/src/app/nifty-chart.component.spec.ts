import {TestBed} from '@angular/core/testing';
import {vi} from 'vitest';
import {NiftyChartComponent} from './nifty-chart.component';
import {TradingWorkspaceSnapshot} from './trading-workspace';

const mocks=vi.hoisted(()=>({series:[] as any[],markers:vi.fn(),removed:vi.fn(),destroy:vi.fn(),paneHeight:vi.fn()}));
// Keep the factory synchronous: Angular's split test bundles can hoist an
// async helper into a shared chunk before that chunk has been initialized.
vi.mock('lightweight-charts',()=>({
  ColorType:{Solid:'solid'},LineStyle:{Dashed:2},LineType:{WithSteps:1},
  CandlestickSeries:{},HistogramSeries:{},LineSeries:{},
  createChart:()=>({
    addSeries:(_type:unknown,options:any,pane=0)=>{
      const series={options,pane,setData:vi.fn(),applyOptions:vi.fn(),
        priceScale:()=>({applyOptions:vi.fn()}),createPriceLine:vi.fn(),removePriceLine:vi.fn()};
      mocks.series.push(series);return series;
    },
    priceScale:()=>({applyOptions:vi.fn()}),panes:()=>[{}, {setHeight:mocks.paneHeight}],
    subscribeClick:vi.fn(),subscribeCrosshairMove:vi.fn(),
    timeScale:()=>({setVisibleLogicalRange:vi.fn()}),removeSeries:mocks.removed,remove:mocks.destroy
  }),
  createSeriesMarkers:(_series:unknown,markers:unknown)=>{mocks.markers(markers);return {setMarkers:mocks.markers};}
}));

describe('shared index chart indicators',()=>{
  beforeEach(()=>{
    mocks.series.length=0;vi.clearAllMocks();localStorage.clear();
    TestBed.configureTestingModule({imports:[NiftyChartComponent]});
  });
  afterEach(()=>{TestBed.resetTestingModule();localStorage.clear();});
  const snapshot=(market:string,offset=0)=>({
    marketCode:market,instrument:market.toUpperCase(),exchange:market==='sensex'?'BSE':'NSE',
    overlays:[],futuresVolume:[],historicalChartEntries:[
      {tradeId:'closed-trade',entryTimeUtc:'2026-11-19T03:50:01Z',entryPrice:120,contract:'25000CE',side:'CE'}],
    candles:Array.from({length:60},(_,i)=>({openTimeUtc:new Date(Date.parse('2026-11-19T03:45:00Z')+i*60000).toISOString(),
      intervalSeconds:60,open:100+offset+i,close:100+offset+i,high:101+offset+i,low:99+offset+i,volume:0,isClosed:true}))
  } as unknown as TradingWorkspaceSnapshot);
  it.each(['nifty','sensex','bank-nifty'])('renders display-only overlays and the separate DMI pane for %s',market=>{
    const fixture=TestBed.createComponent(NiftyChartComponent);
    fixture.componentRef.setInput('snapshot',snapshot(market));fixture.componentRef.setInput('timeframeMinutes',1);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('ADX 100.0');
    expect(mocks.series.find(s=>s.options.title==='ADX 14').pane).toBe(1);
    expect(mocks.series.find(s=>s.options.title==='CH up (22,3)').setData).toHaveBeenCalled();
    expect(mocks.paneHeight).toHaveBeenCalledWith(90);
    expect(mocks.markers.mock.lastCall![0]).toEqual(expect.arrayContaining([expect.objectContaining({text:'CE',shape:'arrowUp'})]));
    const button=[...fixture.nativeElement.querySelectorAll('button')].find((b:any)=>b.textContent==='ADX/DMI') as HTMLButtonElement;
    button.click();fixture.detectChanges();expect(mocks.removed).toHaveBeenCalledTimes(3);
    expect(button.getAttribute('aria-pressed')).toBe('false');
    button.click();fixture.detectChanges();expect(button.getAttribute('aria-pressed')).toBe('true');
    fixture.destroy();expect(mocks.destroy).toHaveBeenCalled();
  });
  it('clears prior-market values on switching and shows warmup on a larger timeframe',()=>{
    const fixture=TestBed.createComponent(NiftyChartComponent);
    fixture.componentRef.setInput('snapshot',snapshot('nifty'));fixture.componentRef.setInput('timeframeMinutes',1);
    fixture.detectChanges();
    fixture.componentRef.setInput('snapshot',snapshot('sensex',80000));fixture.detectChanges();
    const chandelier=mocks.series.find(s=>s.options.title==='CH up (22,3)');
    const data=chandelier.setData.mock.lastCall![0];
    expect(data.filter((p:any)=>p.value!=null).every((p:any)=>p.value>80000)).toBe(true);
    fixture.componentRef.setInput('timeframeMinutes',5);fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('ADX warming up');
    expect(mocks.series.find(s=>s.options.title==='ADX 14').setData.mock.lastCall![0].every((p:any)=>p.value==null)).toBe(true);
  });
});
