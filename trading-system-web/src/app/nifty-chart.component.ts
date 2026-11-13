import {
  AfterViewInit,
  Component,
  ElementRef,
  Input,
  OnChanges,
  OnDestroy,
  SimpleChanges,
  signal,
  ViewChild,
} from '@angular/core';
import {
  CandlestickData,
  CandlestickSeries,
  ColorType,
  createChart,
  createSeriesMarkers,
  HistogramData,
  HistogramSeries,
  IChartApi,
  IPriceLine,
  ISeriesApi,
  ISeriesMarkersPluginApi,
  LineData,
  LineSeries,
  LineStyle,
  Time,
} from 'lightweight-charts';
import { TradingWorkspaceSnapshot, WorkspaceTradeOverlay } from './trading-workspace';
import {
  aggregateCandles,
  aggregateVolumeBars,
  currentSessionLogicalRange,
  latestIstSessionDate,
} from './chart-candles';
import { formatChartTimeIst, formatCrosshairTimeIst } from './chart-time';
import { chartPriceLineTitles } from './chart-labels';
import { chartLevels, ema, sessionVwap } from './chart-overlays';
import { chartEntryFills } from './chart-entry-fills';

@Component({
  selector: 'app-nifty-chart',
  standalone: true,
  template: `
    <div class="chart-shell">
      <details class="engine-read">
        <summary>
          <b>Decision</b>
        @if (snapshot?.engineRead; as d) {
          <span>{{timeIst(d.candleTimeUtc)}} IST · {{humanize(d.outcome)}}</span>
          <span class="decision-preview">{{d.reason || 'View details'}}</span>
        } @else { <span class="decision-preview">No recorded entry decision for this session yet.</span> }
          <span class="decision-expand" aria-hidden="true">⌄</span>
        </summary>
        <div class="decision-details">
          @if (snapshot?.engineRead; as d) {
            <span>{{d.reason || 'No additional reason recorded.'}}</span>
            @if (d.indexConfirmationLevel != null) { <span>Index confirmation {{d.indexConfirmationLevel.toFixed(2)}}</span> }
          } @else { <span>Waiting for the first recorded entry decision.</span> }
        </div>
      </details>
        <div class="overlay-controls" aria-label="Chart overlays">
          <button type="button" [class.active]="visibility.day" [attr.aria-pressed]="visibility.day" (click)="toggle('day')">Day H/L</button>
          <button type="button" [class.active]="visibility.previous" [attr.aria-pressed]="visibility.previous" (click)="toggle('previous')">Prev H/L/C</button>
          <button type="button" [class.active]="visibility.openingRange" [attr.aria-pressed]="visibility.openingRange" (click)="toggle('openingRange')">Opening range</button>
          <button type="button" [class.active]="visibility.vwap" [attr.aria-pressed]="visibility.vwap" (click)="toggle('vwap')">VWAP</button>
          <button type="button" [class.active]="visibility.ema" [attr.aria-pressed]="visibility.ema" (click)="toggle('ema')">EMA 9/21</button>
          @if (rangeText) { <span>{{rangeText}}</span> }
        </div>
      <div class="chart-stage">
        <div #chart class="chart" [attr.aria-label]="'Live ' + snapshot?.instrument + ' candlestick chart'"></div>
        <div class="volume-label">FUTURES VOLUME</div>
        @if (!snapshot?.candles?.length) {
          <div class="chart-empty">
            <strong>Waiting for live {{ snapshot?.instrument || 'market' }} candles</strong>
            <span>{{ snapshot?.statusMessage || 'The chart starts only after verified live data arrives.' }}</span>
          </div>
        }
      </div>
      @if (fills().length) {
        <details class="fill-key">
          <summary>Trade entries ({{fills().length}}) · select a candle for details</summary>
          @for (fill of fills(); track fill.tradeId) { <p>{{fillDetail(fill)}}</p> }
        </details>
        @for (fill of selectedFills; track fill.tradeId) { <p class="fill-readout">{{fillDetail(fill)}}</p> }
      }
    </div>
  `,
  styles: [
    `
      :host,
      .chart-shell {
        display: block;
        min-width: 0;
      }
      .chart-shell { display:grid; grid-template-columns:minmax(0,1fr); align-items:start; background:#edf5e8; }
      .chart-stage { position:relative; min-width:0; min-height:clamp(280px,50dvh,440px); }
      .chart {
        width: 100%;
        height: clamp(280px, 50dvh, 440px);
      }
      .chart-empty {
        position: absolute;
        inset: 0;
        display: grid;
        place-content: center;
        gap: 0.5rem;
        text-align: center;
        color: #557460;
        pointer-events: none;
      }
      .chart-empty strong {
        color: #173f2d;
        font-size: 1.05rem;
      }
      .volume-label {
        position: absolute;
        left: 10px;
        bottom: 25px;
        color: #557460;
        font-size: 0.62rem;
        letter-spacing: 0.08em;
        pointer-events: none;
      }
      .overlay-controls { display:flex; align-items:center; gap:5px; padding:6px 10px; overflow-x:auto; border-bottom:1px solid #c1d7be; background:#e6f0e0; }
      .overlay-controls button { flex-shrink:0; min-height:28px; padding:4px 8px; border:1px solid #a2bea8; border-radius:5px; background:#f2f7ee; color:#42604a; font:inherit; font-size:.68rem; cursor:pointer; white-space:nowrap; }
      .overlay-controls button.active { border-color:#6d987b; color:#205b39; background:#d7e8d1; }
      .overlay-controls button:hover { background:#c9dfc3; }
      .overlay-controls button:focus-visible,.engine-read summary:focus-visible { outline:2px solid #176c3c; outline-offset:2px; }
      .overlay-controls span { margin-left:auto; padding-left:8px; color:#526d5a; font-size:.68rem; white-space:nowrap; }
      .engine-read,.fill-key,.fill-readout { margin:0; padding:4px 9px; border-bottom:1px solid #aac5ac; background:#edf6eb; color:#254c37; font-size:.75rem; }
      .engine-read { padding:0; min-width:0; }
      .engine-read summary { display:flex; align-items:center; gap:8px; min-height:24px; padding:2px 9px; box-sizing:border-box; list-style:none; cursor:pointer; font-size:.72rem; }
      .engine-read summary::-webkit-details-marker { display:none; }
      .engine-read summary > b,.engine-read summary > span:not(.decision-preview) { flex-shrink:0; }
      .decision-preview { overflow:hidden; white-space:nowrap; text-overflow:ellipsis; min-width:0; }
      .decision-expand { margin-left:auto; }
      .engine-read[open] .decision-expand { transform:rotate(180deg); }
      .decision-details { display:grid; gap:3px; padding:5px 9px; border-top:1px solid #c6dac5; overflow-wrap:anywhere; }
      @media(max-width:560px) { .engine-read summary { min-height:30px; } .decision-preview { display:none; } }
      .fill-key summary { cursor:pointer; } .fill-key p { margin:3px 0; }
    `,
  ],
})
export class NiftyChartComponent implements AfterViewInit, OnChanges, OnDestroy {
  @Input({ required: true }) snapshot: TradingWorkspaceSnapshot | null = null;
  @Input() timeframeMinutes = 5;
  @ViewChild('chart', { static: true }) chartElement!: ElementRef<HTMLDivElement>;

  private chart: IChartApi | null = null;
  private series: ISeriesApi<'Candlestick'> | null = null;
  private volumeSeries: ISeriesApi<'Histogram'> | null = null;
  private emaFastSeries: ISeriesApi<'Line'> | null = null;
  private emaSlowSeries: ISeriesApi<'Line'> | null = null;
  private vwapSeries: ISeriesApi<'Line'> | null = null;
  private markerApi: ISeriesMarkersPluginApi<Time> | null = null;
  private priceLines: IPriceLine[] = [];
  private hasFittedContent = false;
  private renderedTimeframeMinutes = 0;
  private renderedInstrument = '';
  private renderedSessionDate: string | null = null;
  protected rangeText = '';
  protected readonly fills = signal<ReturnType<typeof chartEntryFills>>([]);
  private readonly hovered = signal<number|null>(null);
  private readonly pinned = signal<number|null>(null);
  private confirmationSeries: ISeriesApi<'Line'> | null = null;
  private readonly clock = new Intl.DateTimeFormat('en-IN',{ timeZone:'Asia/Kolkata',hour:'2-digit',minute:'2-digit',second:'2-digit',hour12:false });
  protected timeIst(iso:string) { return this.clock.format(Date.parse(iso)); }
  protected humanize(value:string) { return value.replace(/([a-z])([A-Z])/g,'$1 $2'); }
  protected get selectedFills() { return this.fills().filter(f=>f.time===(this.hovered() ?? this.pinned())); }
  protected fillDetail(f:ReturnType<typeof chartEntryFills>[number]) {
    return [f.contract, this.timeIst(f.entryTimeUtc)+' IST', 'Entry ₹'+f.entryPrice.toFixed(2),
      ...(f.evidence ?? [])].join(' · ');
  }
  protected visibility = this.readVisibility();

  ngAfterViewInit(): void {
    this.chart = createChart(this.chartElement.nativeElement, {
      autoSize: true,
      layout: { background: { type: ColorType.Solid, color: '#edf5e8' }, textColor: '#526d5a' },
      grid: { vertLines: { color: '#dfebd9' }, horzLines: { color: '#dfebd9' } },
      rightPriceScale: { borderColor: '#b7cfba' },
      localization: { timeFormatter: formatCrosshairTimeIst },
      timeScale: {
        borderColor: '#b7cfba',
        timeVisible: true,
        secondsVisible: false,
        tickMarkFormatter: formatChartTimeIst,
      },
      crosshair: { vertLine: { color: '#66876d' }, horzLine: { color: '#66876d' } },
      handleScroll: {
        mouseWheel: false,
        pressedMouseMove: true,
        horzTouchDrag: true,
        vertTouchDrag: false,
      },
    });
    this.series = this.chart.addSeries(CandlestickSeries, {
      upColor: '#25834d',
      downColor: '#dc5961',
      borderVisible: false,
      wickUpColor: '#25834d',
      wickDownColor: '#dc5961',
    });
    this.chart.subscribeCrosshairMove(e=>this.hovered.set(typeof e.time==='number'?e.time:null));
    this.chart.subscribeClick(e=>this.pinned.set(typeof e.time==='number' && this.fills().some(f=>f.time===e.time)?e.time:null));
    this.confirmationSeries = this.chart.addSeries(LineSeries,{color:'#895d19',lineStyle:LineStyle.Dashed,
      lineWidth:2,lastValueVisible:false,priceLineVisible:false,crosshairMarkerVisible:false,pointMarkersVisible:true});
    this.series.priceScale().applyOptions({ scaleMargins: { top: 0.08, bottom: 0.24 } });
    this.volumeSeries = this.chart.addSeries(HistogramSeries, {
      priceFormat: { type: 'volume' },
      priceScaleId: 'volume',
      lastValueVisible: false,
      priceLineVisible: false,
    });
    this.emaFastSeries = this.chart.addSeries(LineSeries, { title:'EMA9', color:'#54a86d', lineWidth:1, lastValueVisible:true, priceLineVisible:false });
    this.emaSlowSeries = this.chart.addSeries(LineSeries, { title:'EMA21', color:'#286f46', lineWidth:1, lastValueVisible:true, priceLineVisible:false });
    this.vwapSeries = this.chart.addSeries(LineSeries, { title:'VWAP', color:'#f2c94c', lineWidth:2, lastValueVisible:true, priceLineVisible:false });
    this.chart.priceScale('volume').applyOptions({
      scaleMargins: { top: 0.82, bottom: 0 },
      borderVisible: false,
    });
    this.render();
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['timeframeMinutes']) this.hasFittedContent = false;
    if (changes['snapshot']) this.render();
    else if (changes['timeframeMinutes']) this.render();
  }

  ngOnDestroy(): void {
    this.chart?.remove();
  }

  private render(): void {
    if (!this.series || !this.chart || !this.snapshot) return;

    const instrumentIdentity = `${this.snapshot.exchange}:${this.snapshot.instrument}`;
    if (instrumentIdentity !== this.renderedInstrument) {
      this.hasFittedContent = false;
      this.renderedInstrument = instrumentIdentity;
      this.pinned.set(null); this.hovered.set(null);
      this.renderedSessionDate = null;
    }
    const displayCandles = aggregateCandles(this.snapshot.candles, this.timeframeMinutes);
    const latestSessionDate = latestIstSessionDate(displayCandles);
    const candles: CandlestickData<Time>[] = displayCandles.map((value) => ({
      time: Math.floor(new Date(value.openTimeUtc).getTime() / 1000) as Time,
      open: value.open,
      high: value.high,
      low: value.low,
      close: value.close,
    }));
    this.series.setData(candles);
    const market = this.snapshot.marketCode?.toLowerCase() ?? (this.snapshot.instrument.includes('BANK') ? 'bank-nifty' : this.snapshot.exchange === 'BSE' ? 'sensex' : 'nifty');
    const mark = 'desk:chart-request:' + market;
    const candleDirectionByTime = new Map(
      displayCandles.map((value) => [
        Math.floor(new Date(value.openTimeUtc).getTime() / 1000),
        value.close >= value.open,
      ]),
    );
    const volumeBars = aggregateVolumeBars(
      this.snapshot.futuresVolume ?? [],
      this.timeframeMinutes,
    );
    const volume: HistogramData<Time>[] = volumeBars.map((value) => {
      const time = Math.floor(new Date(value.openTimeUtc).getTime() / 1000);
      return {
        time: time as Time,
        value: value.volume,
        color: candleDirectionByTime.get(time) === false ? '#ff5d6870' : '#28d17c70',
      };
    });
    this.volumeSeries?.setData(volume);
    const lineData = (points:{openTimeUtc:string;value:number}[]):LineData<Time>[] => points.map(value => ({
      time: Math.floor(new Date(value.openTimeUtc).getTime()/1000) as Time, value:value.value,
    }));
    this.emaFastSeries?.setData(lineData(ema(displayCandles,9)));
    this.emaSlowSeries?.setData(lineData(ema(displayCandles,21)));
    this.vwapSeries?.setData(lineData(sessionVwap(displayCandles,volumeBars)));
    this.emaFastSeries?.applyOptions({visible:this.visibility.ema});
    this.emaSlowSeries?.applyOptions({visible:this.visibility.ema});
    this.vwapSeries?.applyOptions({visible:this.visibility.vwap});
    this.priceLines.forEach((line) => this.series?.removePriceLine(line));
    this.priceLines = [];
    const levels=chartLevels(this.snapshot.candles);
    this.rangeText=levels ? `Range ${levels.dayRange.toFixed(2)} · ${levels.dayRangePercent.toFixed(2)}%` : '';
    if(this.visibility.openingRange && levels?.openingRangeHigh==null) this.rangeText += ' · Opening range unavailable (09:15–09:30 IST)';
    const levelLine=(price:number|null,title:string,color:string,style:LineStyle=LineStyle.Dashed):void=>{
      if(price==null)return;
      this.priceLines.push(this.series!.createPriceLine({price,color,lineWidth:1,lineStyle:style,title}));
    };
    if(levels&&this.visibility.day){levelLine(levels.dayHigh,'DAY HIGH','#2ad18a');levelLine(levels.dayLow,'DAY LOW','#ff7380');}
    if(levels&&this.visibility.previous){levelLine(levels.previousHigh,'PREV HIGH','#679476');levelLine(levels.previousLow,'PREV LOW','#679476');levelLine(levels.previousClose,'PREV CLOSE','#879c89',LineStyle.Dotted);}
    if(levels&&this.visibility.openingRange){levelLine(levels.openingRangeHigh,'OR HIGH','#ae8b35');levelLine(levels.openingRangeLow,'OR LOW','#ae8b35');}
    const overlay = this.latestOverlay();
    if (overlay && !(overlay.entry===overlay.target && overlay.entry===overlay.stopLoss)) {
      const entry = overlay.entry;
      const lineTitles = chartPriceLineTitles(this.snapshot, overlay.direction);
      this.priceLines.push(
        this.series.createPriceLine({
          price: entry,
          color: '#f2c94c',
          lineWidth: 2,
          title: lineTitles.entry,
        }),
        this.series.createPriceLine({
          price: overlay.stopLoss,
          color: '#ff5d68',
          lineWidth: 2,
          title: lineTitles.stop,
        }),
        this.series.createPriceLine({
          price: overlay.target,
          color: '#28d17c',
          lineWidth: 2,
          title: lineTitles.target,
        }),
      );
    }
    const decision = this.snapshot.engineRead;
    const when = decision ? Date.parse(decision.candleTimeUtc) : NaN;
    const index = displayCandles.findIndex(c=>{const start=Date.parse(c.openTimeUtc);return start<=when && when<start+c.intervalSeconds*1000;});
    const price = decision?.indexConfirmationLevel;
    const segment = index>=0 && price!=null && price>0 ? displayCandles.slice(index,index+2)
      .filter(c=>Date.parse(c.openTimeUtc)-Date.parse(displayCandles[index].openTimeUtc)<=displayCandles[index].intervalSeconds*1000)
      .map(c=>({time:(Date.parse(c.openTimeUtc)/1000) as Time,value:price})) : [];
    this.confirmationSeries?.setData(segment);
    const fills = chartEntryFills(this.snapshot.overlays, displayCandles, this.snapshot.historicalChartEntries ?? []);
    this.fills.set(fills);
    const markers = fills.map(f=>({time:f.time as Time,
      position:f.side==='CE'?'belowBar' as const:'aboveBar' as const,
      color:f.side==='CE'?'#167143':'#a9363e',shape:f.side==='CE'?'arrowUp' as const:'arrowDown' as const,
      text:f.side}));
    if (this.markerApi) this.markerApi.setMarkers(markers);
    else this.markerApi = createSeriesMarkers(this.series, markers);
    if (candles.length && (
      !this.hasFittedContent ||
      this.renderedTimeframeMinutes !== this.timeframeMinutes ||
      this.renderedSessionDate === null ||
      (latestSessionDate !== null && latestSessionDate > this.renderedSessionDate)
    )) {
      const sessionMinutes = this.snapshot.exchange === 'MCX' ? 870 : 375;
      const initialRange = currentSessionLogicalRange(
        displayCandles,
        this.timeframeMinutes,
        sessionMinutes,
        volume.map((bar) => new Date((bar.time as number) * 1000).toISOString()),
      );
      if (initialRange) {
        this.chart.timeScale().setVisibleLogicalRange(initialRange);
      }
      this.hasFittedContent = true;
      this.renderedTimeframeMinutes = this.timeframeMinutes;
      this.renderedSessionDate = latestSessionDate;
    }
    const started = candles.length ? performance.getEntriesByName(mark, 'mark').at(-1) : undefined;
    if (started) {
      const ready = 'desk:chart-series-ready:' + market;
      performance.clearMeasures(ready);
      performance.measure(ready, mark);
      performance.clearMarks(mark);
      // Include the chart library's scheduled canvas draw and a visible frame.
      requestAnimationFrame(() => requestAnimationFrame(() => {
        if (!this.chart || this.renderedInstrument !== instrumentIdentity || document.hidden) return;
        const name = 'desk:index-switch-visible:' + market;
        performance.clearMeasures(name);
        performance.measure(name, { start: started.startTime, end: performance.now() });
      }));
    }
  }

  protected toggle(key:keyof ChartOverlayVisibility):void {
    this.visibility={...this.visibility,[key]:!this.visibility[key]};
    localStorage.setItem('sarthi.chartOverlays',JSON.stringify(this.visibility));
    this.render();
  }

  private readVisibility():ChartOverlayVisibility {
    const defaults:ChartOverlayVisibility={day:true,previous:true,openingRange:true,vwap:true,ema:true};
    try{return {...defaults,...JSON.parse(localStorage.getItem('sarthi.chartOverlays')||'{}')};}catch{return defaults;}
  }

  private latestOverlay(): WorkspaceTradeOverlay | null {
    if (this.snapshot?.exchange === 'MCX') return null;
    return this.snapshot?.overlays?.find(trade => this.isActiveOptionTrade(trade)) ?? null;
  }

  private isActiveOptionTrade(trade: WorkspaceTradeOverlay): boolean {
    return trade.fillPrice !== null && trade.exitTimeUtc === null &&
      (trade.lifecycleStatus.startsWith('Active') || trade.lifecycleStatus === 'Entry filled') &&
      (trade.executionInstrument?.endsWith('CE') || trade.executionInstrument?.endsWith('PE') ||
        trade.executionInstrumentType?.includes('Option')) === true;
  }

}

interface ChartOverlayVisibility { day:boolean; previous:boolean; openingRange:boolean; vwap:boolean; ema:boolean; }
