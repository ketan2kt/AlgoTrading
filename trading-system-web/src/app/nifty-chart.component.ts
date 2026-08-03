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
  IChartApi,
  IPriceLine,
  ISeriesApi,
  ISeriesMarkersPluginApi,
  Time,
} from 'lightweight-charts';
import { TradingWorkspaceSnapshot, WorkspaceTradeOverlay } from './trading-workspace';
import { aggregateCandles } from './chart-candles';
import { formatChartTimeIst, formatCrosshairTimeIst } from './chart-time';

@Component({
  selector: 'app-nifty-chart',
  standalone: true,
  template: `
    <div class="chart-shell">
      <div #chart class="chart" aria-label="Live Nifty candlestick chart"></div>
      @if (!snapshot?.candles?.length) {
        <div class="chart-empty">
          <strong>Waiting for live Nifty candles</strong>
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
        min-height: 460px;
      }
      .chart {
        width: 100%;
        height: 460px;
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
  private markerApi: ISeriesMarkersPluginApi<Time> | null = null;
  private resizeObserver: ResizeObserver | null = null;
  private priceLines: IPriceLine[] = [];
  private hasFittedContent = false;
  private renderedTimeframeMinutes = 0;

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
    const displayCandles = aggregateCandles(this.snapshot.candles, this.timeframeMinutes);
    const candles: CandlestickData<Time>[] = displayCandles.map((value) => ({
      time: Math.floor(new Date(value.openTimeUtc).getTime() / 1000) as Time,
      open: value.open,
      high: value.high,
      low: value.low,
      close: value.close,
    }));
    this.series.setData(candles);
    this.priceLines.forEach((line) => this.series?.removePriceLine(line));
    this.priceLines = [];
    const overlay = this.latestOverlay();
    if (overlay) {
      const entry = overlay.fillPrice ?? overlay.entry;
      this.priceLines = [
        this.series.createPriceLine({
          price: entry,
          color: '#f2c94c',
          lineWidth: 2,
          title: 'ENTRY',
        }),
        this.series.createPriceLine({
          price: overlay.stopLoss,
          color: '#ff5d68',
          lineWidth: 2,
          title: 'SL',
        }),
        this.series.createPriceLine({
          price: overlay.target,
          color: '#28d17c',
          lineWidth: 2,
          title: 'TARGET',
        }),
      ];
      const position = overlay.direction === 'Buy' ? 'belowBar' : 'aboveBar';
      this.markerApi = createSeriesMarkers(this.series, [
        {
          time: Math.floor(new Date(overlay.signalTimeUtc).getTime() / 1000) as Time,
          position,
          color: '#f2c94c',
          shape: overlay.direction === 'Buy' ? 'arrowUp' : 'arrowDown',
          text: `${overlay.strategy} · ${overlay.status}`,
        },
      ]);
    } else {
      this.markerApi?.setMarkers([]);
    }
    if (candles.length && (!this.hasFittedContent || this.renderedTimeframeMinutes !== this.timeframeMinutes)) {
      this.chart.timeScale().fitContent();
      this.hasFittedContent = true;
      this.renderedTimeframeMinutes = this.timeframeMinutes;
    }
    this.positionZones(overlay);
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
    const entry = overlay.fillPrice ?? overlay.entry;
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
