import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, computed, input, output } from '@angular/core';
import type { DailyDiagnostics } from './paper-report.service';

@Component({
  selector: 'app-daily-profit-review',
  imports: [DatePipe, DecimalPipe],
  templateUrl: './daily-profit-review.component.html',
  styleUrl: './daily-profit-review.component.scss',
})
export class DailyProfitReviewComponent {
  readonly report = input.required<DailyDiagnostics>();
  readonly replay = output<string>();
  protected readonly rows = computed(() => {
    const trades = new Map(this.report().trades.map(t => [t.tradeId, t]));
    return (this.report().profitReview?.trades ?? []).map(review => ({ review, trade: trades.get(review.tradeId) }));
  });
}
