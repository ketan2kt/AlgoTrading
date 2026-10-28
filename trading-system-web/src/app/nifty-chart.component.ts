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
  currentSessionLogicalRange,
  latestIstSessionDate,
} from './chart-candles';
import { formatChartTimeIst, formatCrosshairTimeIst } from './chart-time';
import { chartPriceLineTitles } from './chart-labels';
import { chartLevels, ema, entryExplanation, sessionVwap } from './chart-overlays';
import { buildDecisionMap, DecisionMapView } from './decision-map';

@Component({
  selector: 'app-nifty-chart',
  standalone: true,
  template: `
    <div class="chart-shell">
      @if (decisionMap; as map) {
        <details class="decision-map" aria-label="Live decision map">
          <summary>
            <span>MARKET READ</span>
            <strong>{{map.regime}} · {{map.bias}} · {{map.setup}}</strong>
            <small>{{map.location}} · {{map.remainingRoom}}</small>
          </summary>
          <div class="decision-map__grid">
            <div><span>REGIME</span><strong>{{map.regime}}</strong></div>
            <div><span>BIAS</span><strong>{{map.bias}}</strong></div>
            <div><span>SETUP</span><strong>{{map.setup}}</strong></div>
            <div><span>LOCATION</span><strong>{{map.location}}</strong></div>
            <div><span>ROOM</span><strong>{{map.remainingRoom}}</strong></div>
            <div><span>VOLUME</span><strong>{{map.volume}}</strong></div>
          </div>
          <div class="decision-map__frames">
            @for (frame of map.timeframes; track frame.label) {
              <small [attr.data-direction]="frame.direction">{{frame.label}} {{frame.direction}}</small>
            }
          </div>
          <p>{{map.reasons[0]}}</p>
        </details>
      }
      <div class="chart-stage">
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
            <span>{{ snapshot?.statusMessage || 'The chart starts only after verified live data arrives.' }}</span>
          </div>
        }
      </div>
    </div>
  `,
  styles: [
    `
      :host,
      .chart-shell {
        display: block;
        min-height: clamp(340px, calc(70vh - 110px), 520px);
      }
      .chart-shell { display:grid; grid-template-columns:minmax(0,1fr); align-items:start; background:#e6f2e2; }
      .chart-stage { position:relative; min-width:0; min-height:clamp(340px,calc(70vh - 110px),520px); }
      .chart {
        width: 100%;
        height: clamp(340px, calc(70vh - 110px), 520px);
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
      .overlay-controls { position:absolute; z-index:2; top:7px; left:8px; display:flex; flex-wrap:wrap; gap:4px; max-width:calc(100% - 110px); }
      .overlay-controls button { padding:3px 7px; border:1px solid #a9c6ad; border-radius:5px; background:#e6f2e2ee; color:#315b43; font-size:.7rem; }
      .overlay-controls button.active { border-color:#25834d; color:#176c3c; background:#e5f1e3; }
      .overlay-controls span { align-self:center; padding:2px 6px; color:#315b43; font-size:.7rem; background:#e6f2e2ee; }
      .decision-map { margin:0; border:0; border-bottom:1px solid #c6dac5; background:#edf6eb; }
      .decision-map summary { display:grid; grid-template-columns:auto minmax(180px,1fr) auto; gap:10px; align-items:center; min-height:34px; padding:5px 10px; cursor:pointer; list-style:none; }
      .decision-map summary::-webkit-details-marker { display:none; }
      .decision-map summary::after { content:'Details'; color:#34734b; font-size:.62rem; letter-spacing:.08em; text-transform:uppercase; }
      .decision-map[open] summary::after { content:'Hide'; }
      .decision-map summary span { color:#557460; font-size:.62rem; letter-spacing:.11em; }
      .decision-map summary strong { overflow:hidden; color:#173f2d; font-size:.76rem; text-overflow:ellipsis; white-space:nowrap; }
      .decision-map summary small { overflow:hidden; color:#557460; font-size:.7rem; text-overflow:ellipsis; white-space:nowrap; }
      .decision-map__grid { display:grid; grid-template-columns:repeat(3,minmax(0,1fr)); gap:1px; padding:2px 8px 5px; }
      .decision-map__grid div { min-width:0; padding:3px 5px; }
      .decision-map span { display:block; color:#557460; font-size:.62rem; letter-spacing:.09em; }
      .decision-map strong { display:block; overflow:hidden; color:#173f2d; font-size:.76rem; text-overflow:ellipsis; white-space:nowrap; }
      .decision-map__frames { display:flex; gap:5px; padding:0 13px 5px; }
      .decision-map__frames small { padding:2px 5px; border-radius:4px; background:#dcebd9; color:#315b43; font-size:.66rem; }
      .decision-map__frames small[data-direction='Bullish'] { color:#167143; }
      .decision-map__frames small[data-direction='Bearish'] { color:#a9363e; }
      .decision-map p { margin:0; padding:2px 13px 7px; color:#557460; font-size:.7rem; }
      @media (max-width: 900px) {
        .decision-map summary { grid-template-columns:auto minmax(120px,1fr); }
        .decision-map summary small { display:none; }
        .decision-map__grid { grid-template-columns:repeat(2,minmax(0,1fr)); }
      }
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
  protected decisionMap: DecisionMapView | null = null;
  protected visibility = this.readVisibility();

  ngAfterViewInit(): void {
    this.chart = createChart(this.chartElement.nativeElement, {
      autoSize: true,
      layout: { background: { type: ColorType.Solid, color: '#e6f2e2' }, textColor: '#45644d' },
      grid: { vertLines: { color: '#c9dec7' }, horzLines: { color: '#c9dec7' } },
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
    this.decisionMap = buildDecisionMap(this.snapshot);
    const instrumentIdentity = `${this.snapshot.exchange}:${this.snapshot.instrument}`;
    if (instrumentIdentity !== this.renderedInstrument) {
      this.hasFittedContent = false;
      this.renderedInstrument = instrumentIdentity;
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
    const levelLine=(price:number|null,title:string,color:string,style:LineStyle=LineStyle.Dashed):void=>{
      if(price==null)return;
      this.priceLines.push(this.series!.createPriceLine({price,color,lineWidth:1,lineStyle:style,title}));
    };
    if(levels&&this.visibility.day){levelLine(levels.dayHigh,'DAY HIGH','#2ad18a');levelLine(levels.dayLow,'DAY LOW','#ff7380');}
    if(levels&&this.visibility.previous){levelLine(levels.previousHigh,'PREV HIGH','#679476');levelLine(levels.previousLow,'PREV LOW','#679476');levelLine(levels.previousClose,'PREV CLOSE','#879c89',LineStyle.Dotted);}
    if(levels&&this.visibility.openingRange){levelLine(levels.openingRangeHigh,'OR HIGH','#ae8b35');levelLine(levels.openingRangeLow,'OR LOW','#ae8b35');}
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
    }
    const activeTrades = this.snapshot.overlays.filter(value => this.isActiveOptionTrade(value));
    const activeTradeIds = new Set(activeTrades.map(value => value.signalId));
    const tradeMarkers = activeTrades.map(value => ({
      time: Math.floor(new Date(value.signalTimeUtc).getTime() / 1000) as Time,
      position: (value.direction === 'Buy' ? 'belowBar' : 'aboveBar') as 'belowBar'|'aboveBar',
      color: '#f2c94c',
      shape: (value.direction === 'Buy' ? 'arrowUp' : 'arrowDown') as 'arrowUp'|'arrowDown',
      text: entryExplanation(value,this.snapshot!.evaluations),
    }));
    const acceptedMarkers = this.snapshot.evaluations.filter(value =>
      value.outcome === 'PaperPositionOpened' && value.signalId !== null &&
      activeTradeIds.has(value.signalId)).slice(0,5).map(value => ({
      time: Math.floor(new Date(value.candleTimeUtc).getTime() / 1000) as Time,
      position: 'aboveBar' as const,
      color: '#28d17c',
      shape: 'circle' as const,
      text: 'ENTRY ACCEPTED',
    }));
    const latestRejection = this.snapshot.evaluations.filter(value =>
      value.outcome.includes('Rejected')).slice(0,1).map(value => ({
      time: Math.floor(new Date(value.candleTimeUtc).getTime() / 1000) as Time,
      position: 'aboveBar' as const,
      color: '#83998e',
      shape: 'circle' as const,
      text: 'LATEST SETUP REJECTED',
    }));
    const visibleTimes = new Set(candles.map(value => value.time as number));
    const markers = [...latestRejection, ...acceptedMarkers, ...tradeMarkers]
      .filter(value => visibleTimes.has(value.time as number))
      .sort((left,right)=>(left.time as number)-(right.time as number));
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
