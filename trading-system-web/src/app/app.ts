import { AsyncPipe } from '@angular/common';
import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { catchError, of, startWith } from 'rxjs';
import { SystemStatusService } from './system-status.service';

@Component({
  selector: 'app-root',
  imports: [AsyncPipe, RouterOutlet],
  templateUrl: './app.html',
  styleUrl: './app.scss'
})
export class App {
  private readonly statusService = inject(SystemStatusService);

  protected readonly status$ = this.statusService.getCurrent().pipe(
    startWith({
      mode: 'Paper' as const,
      liveTradingAvailable: false,
      tradingEnabled: false,
      status: 'Loading',
      observedAtUtc: ''
    }),
    catchError(() =>
      of({
        mode: 'Paper' as const,
        liveTradingAvailable: false,
        tradingEnabled: false,
        status: 'Unavailable',
        observedAtUtc: ''
      })
    )
  );
}
