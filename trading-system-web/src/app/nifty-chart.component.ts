import {
  AfterViewInit,
  Component,
  ElementRef,
  Input,
  OnChanges,
  OnDestroy,
  SimpleChanges,
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
  currentSessionTimeRange,
  latestIstSessionDate,
} from './chart-candles';
import { formatChartTimeIst, formatCrosshairTimeIst } from './chart-time';
import { chartPriceLineTitles } from './chart-labels';
import { chartLevels, ema, entryExplanation, sessionVwap } from './chart-overlays';

@Component({
  selector: 'app-nifty-chart',
  standalone: true,
  template: `
    <div class="chart-shell">
      <div class="overlay-controls" aria-label="Chart overlays">
        <button type="button" [class.active]="visibility.day" (click)="toggle('day')">Day H/L</button>
        <button type="button" [class.active]="visibility.previous" (click)="toggle('previous')">Prev H/L/C</button>
        <button type="button" [class.active]="visibility.openingRange" (click)="toggle('openingRange')">Opening range</button>
        <button type="button" [class.active]="visibility.vwap" (click)="toggle('vwap')">VWAP</button>
        <button type="button" [class.active]="visibility.ema" (click)="toggle('ema')">EMA 9/21</button>
        @if (rangeText) { <span>{{rangeText}}</span> }
      </div>
      <div #chart class="chart" [attr.aria-label]="'Live ' + snapshot?.instrument + ' candlestick chart'"></div>
      <div class="volume-label">{{ snapshot?.instrument }} VOLUME</div>
      @if (!snapshot?.candles?.length) {
        <div class="chart-empty">
          <strong>Waiting for live {{ snapshot?.instrument || 'market' }} candles</strong>
          <span>{{
            snapshot?.statusMessage || 'The chart starts only after verified live data arrives.'
          }}</span>
        </div>
      }
      <div #rewardZone class="trade-zone trade-zone--reward"></div>
      <div #riskZone class="trade-zone trade-zone--risk"></div>
    </div>
  `,
  styles: [
    `
      :host,
      .chart-shell {
        display: block;
        position: relative;
        min-height: clamp(420px, calc(100vh - 110px), 680px);
      }
      .chart {
        width: 100%;
        height: clamp(420px, calc(100vh - 110px), 680px);
      }
      .chart-empty {
        position: absolute;
        inset: 0;
        display: grid;
        place-content: center;
        gap: 0.5rem;
        text-align: center;
        color: #91a39a;
        pointer-events: none;
      }
      .chart-empty strong {
        color: #e9f2ed;
        font-size: 1.05rem;
      }
      .volume-label {
        position: absolute;
        left: 10px;
        bottom: 25px;
        color: #789087;
        font-size: 0.62rem;
        letter-spacing: 0.08em;
        pointer-events: none;
      }
      .overlay-controls { position:absolute; z-index:2; top:7px; left:8px; display:flex; flex-wrap:wrap; gap:4px; max-width:calc(100% - 110px); }
      .overlay-controls button { padding:3px 7px; border:1px solid #30433a; border-radius:4px; background:#0d1713dd; color:#82978c; font-size:.62rem; }
      .overlay-controls button.active { border-color:#62ba91; color:#bce8d2; }
      .overlay-controls span { align-self:center; padding:2px 6px; color:#b6c6be; font-size:.64rem; background:#0d1713dd; }
      .trade-zone {
        position: absolute;
        right: 64px;
        width: 24%;
        pointer-events: none;
        opacity: 0.15;
        display: none;
      }
      .trade-zone--reward {
        background: #27d17f;
        border: 1px solid #42e899;
      }
      .trade-zone--risk {
        background: #ff5d68;
        border: 1px solid #ff7881;
      }
    `,
  ],
})
export class NiftyChartComponent implements AfterViewInit, OnChanges, OnDestroy {
  @Input({ required: true }) snapshot: TradingWorkspaceSnapshot | null = null;
  @Input() timeframeMinutes = 5;
  @ViewChild('chart', { static: true }) chartElement!: ElementRef<HTMLDivElement>;
  @ViewChild('rewardZone', { static: true }) rewardZone!: ElementRef<HTMLDivElement>;
  @ViewChild('riskZone', { static: true }) riskZone!: ElementRef<HTMLDivElement>;

  private chart: IChartApi | null = null;
  private series: ISeriesApi<'Candlestick'> | null = null;
  private volumeSeries: ISeriesApi<'Histogram'> | null = null;
  private emaFastSeries: ISeriesApi<'Line'> | null = null;
  private emaSlowSeries: ISeriesApi<'Line'> | null = null;
  private vwapSeries: ISeriesApi<'Line'> | null = null;
  private markerApi: ISeriesMarkersPluginApi<Time> | null = null;
  private resizeObserver: ResizeObserver | null = null;
  private priceLines: IPriceLine[] = [];
  private hasFittedContent = false;
  private renderedTimeframeMinutes = 0;
  private renderedInstrument = '';
  private renderedSessionDate: string | null = null;
  protected rangeText = '';
  protected visibility = this.readVisibility();

  ngAfterViewInit(): void {
    this.chart = createChart(this.chartElement.nativeElement, {
      autoSize: true,
      layout: { background: { type: ColorType.Solid, color: '#0d1512' }, textColor: '#9caea5' },
      grid: { vertLines: { color: '#1c2924' }, horzLines: { color: '#1c2924' } },
      rightPriceScale: { borderColor: '#2c3a34' },
      localization: { timeFormatter: formatCrosshairTimeIst },
      timeScale: {
        borderColor: '#2c3a34',
        timeVisible: true,
        secondsVisible: false,
        tickMarkFormatter: formatChartTimeIst,
      },
      crosshair: { vertLine: { color: '#658c7b' }, horzLine: { color: '#658c7b' } },
      handleScroll: {
        mouseWheel: false,
        pressedMouseMove: true,
        horzTouchDrag: true,
        vertTouchDrag: false,
      },
    });
    this.series = this.chart.addSeries(CandlestickSeries, {
      upColor: '#28d17c',
      downColor: '#ff5d68',
      borderVisible: false,
      wickUpColor: '#28d17c',
      wickDownColor: '#ff5d68',
    });
    this.series.priceScale().applyOptions({ scaleMargins: { top: 0.08, bottom: 0.24 } });
    this.volumeSeries = this.chart.addSeries(HistogramSeries, {
      priceFormat: { type: 'volume' },
      priceScaleId: 'volume',
      lastValueVisible: false,
      priceLineVisible: false,
    });
    this.emaFastSeries = this.chart.addSeries(LineSeries, { title:'EMA9', color:'#68a7ff', lineWidth:1, lastValueVisible:true, priceLineVisible:false });
    this.emaSlowSeries = this.chart.addSeries(LineSeries, { title:'EMA21', color:'#b68cff', lineWidth:1, lastValueVisible:true, priceLineVisible:false });
    this.vwapSeries = this.chart.addSeries(LineSeries, { title:'VWAP', color:'#f2c94c', lineWidth:2, lastValueVisible:true, priceLineVisible:false });
    this.chart.priceScale('volume').applyOptions({
      scaleMargins: { top: 0.82, bottom: 0 },
      borderVisible: false,
    });
    this.resizeObserver = new ResizeObserver(() => this.positionZones(this.latestOverlay()));
    this.resizeObserver.observe(this.chartElement.nativeElement);
    this.render();
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['timeframeMinutes']) this.hasFittedContent = false;
    if (changes['snapshot']) this.render();
    else if (changes['timeframeMinutes']) this.render();
  }

  ngOnDestroy(): void {
    this.resizeObserver?.disconnect();
    this.chart?.remove();
  }

  private render(): void {
    if (!this.series || !this.chart || !this.snapshot) return;
    const instrumentIdentity = `${this.snapshot.exchange}:${this.snapshot.instrument}`;
    if (instrumentIdentity !== this.renderedInstrument) {
      this.hasFittedContent = false;
      this.renderedInstrument = instrumentIdentity;
      this.renderedSessionDate = null;
    }
    const displayCandles = aggregateCandles(this.snapshot.candles, this.timeframeMinutes);
    const candles: CandlestickData<Time>[] = displayCandles.map((value) => ({
      time: Math.floor(new Date(value.openTimeUtc).getTime() / 1000) as Time,
      open: value.open,
      high: value.high,
      low: value.low,
      close: value.close,
    }));
    this.series.setData(candles);
    const candleDirectionByTime = new Map(
      displayCandles.map((value) => [
        Math.floor(new Date(value.openTimeUtc).getTime() / 1000),
        value.close >= value.open,
      ]),
    );
    const volume: HistogramData<Time>[] = aggregateVolumeBars(
      this.snapshot.futuresVolume ?? [],
      this.timeframeMinutes,
    ).map((value) => {
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
    this.vwapSeries?.setData(lineData(sessionVwap(this.snapshot.candles,this.snapshot.futuresVolume ?? [])));
    this.emaFastSeries?.applyOptions({visible:this.visibility.ema});
    this.emaSlowSeries?.applyOptions({visible:this.visibility.ema});
    this.vwapSeries?.applyOptions({visible:this.visibility.vwap});
    this.priceLines.forEach((line) => this.series?.removePriceLine(line));
    this.priceLines = [];
    const levels=chartLevels(this.snapshot.candles);
    this.rangeText=levels ? `Range ${levels.dayRange.toFixed(2)} · ${levels.dayRangePercent.toFixed(2)}%` : '';
    const levelLine=(price:number|null,title:string,color:string,style:LineStyle=LineStyle.Dashed):void=>{
      if(price==null)return;
      this.priceLines.push(this.series!.createPriceLine({price,color,lineWidth:1,lineStyle:style,title}));
    };
    if(levels&&this.visibility.day){levelLine(levels.dayHigh,'DAY HIGH','#2ad18a');levelLine(levels.dayLow,'DAY LOW','#ff7380');}
    if(levels&&this.visibility.previous){levelLine(levels.previousHigh,'PREV HIGH','#5f8fb5');levelLine(levels.previousLow,'PREV LOW','#5f8fb5');levelLine(levels.previousClose,'PREV CLOSE','#7f8d86',LineStyle.Dotted);}
    if(levels&&this.visibility.openingRange){levelLine(levels.openingRangeHigh,'OR HIGH','#d49a4b');levelLine(levels.openingRangeLow,'OR LOW','#d49a4b');}
    const overlay = this.latestOverlay();
    if (overlay) {
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
      const position = overlay.direction === 'Buy' ? 'belowBar' : 'aboveBar';
      this.markerApi = createSeriesMarkers(this.series, [
        {
          time: Math.floor(new Date(overlay.signalTimeUtc).getTime() / 1000) as Time,
          position,
          color: '#f2c94c',
          shape: overlay.direction === 'Buy' ? 'arrowUp' : 'arrowDown',
          text: entryExplanation(overlay,this.snapshot.evaluations),
        },
      ]);
    } else {
      this.markerApi?.setMarkers([]);
    }
    const latestSessionDate = latestIstSessionDate(displayCandles);
    if (candles.length && (
      !this.hasFittedContent ||
      this.renderedTimeframeMinutes !== this.timeframeMinutes ||
      this.renderedSessionDate === null ||
      (latestSessionDate !== null && latestSessionDate > this.renderedSessionDate)
    )) {
      const sessionMinutes = this.snapshot.exchange === 'MCX' ? 870 : 375;
      const initialRange = currentSessionTimeRange(displayCandles, sessionMinutes);
      if (initialRange) {
        this.chart.timeScale().setVisibleRange({
          from: initialRange.from as Time,
          to: initialRange.to as Time,
        });
      }
      this.hasFittedContent = true;
      this.renderedTimeframeMinutes = this.timeframeMinutes;
      this.renderedSessionDate = latestSessionDate;
    }
    this.positionZones(overlay);
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
    return this.snapshot?.overlays?.[0] ?? null;
  }

  private positionZones(overlay: WorkspaceTradeOverlay | null): void {
    if (!this.series || !overlay) {
      this.rewardZone.nativeElement.style.display = 'none';
      this.riskZone.nativeElement.style.display = 'none';
      return;
    }
    const entry = overlay.entry;
    this.setZone(this.rewardZone.nativeElement, entry, overlay.target);
    this.setZone(this.riskZone.nativeElement, entry, overlay.stopLoss);
  }

  private setZone(element: HTMLDivElement, first: number, second: number): void {
    const firstY = this.series?.priceToCoordinate(first);
    const secondY = this.series?.priceToCoordinate(second);
    if (firstY == null || secondY == null) {
      element.style.display = 'none';
      return;
    }
    element.style.display = 'block';
    element.style.top = `${Math.min(firstY, secondY)}px`;
    element.style.height = `${Math.max(2, Math.abs(firstY - secondY))}px`;
  }
}

interface ChartOverlayVisibility { day:boolean; previous:boolean; openingRange:boolean; vwap:boolean; ema:boolean; }
