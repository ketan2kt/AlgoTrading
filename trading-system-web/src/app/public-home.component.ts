import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectorRef, Component, inject, OnDestroy, OnInit } from '@angular/core';
import { FormsModule, NgForm } from '@angular/forms';

interface ContactConfig { enabled: boolean; siteKey: string | null }
interface RecaptchaApi {
  render(target: HTMLElement, options: Record<string, unknown>): number;
  reset(widgetId: number): void;
}

@Component({
  selector: 'app-public-home',
  imports: [FormsModule],
  templateUrl: './public-home.component.html',
  styleUrl: './public-home.component.scss',
})
export class PublicHomeComponent implements OnInit, OnDestroy {
  private readonly http = inject(HttpClient);
  private readonly view = inject(ChangeDetectorRef);
  private captcha: RecaptchaApi | undefined;
  private widgetId: number | undefined;
  private disposed = false;
  protected config: ContactConfig | null = null;
  protected captchaToken = '';
  protected busy = false;
  protected feedback = '';
  protected error = '';
  protected fields = { name: '', email: '', company: '', service: '', message: '', website: '' };

  ngOnInit(): void {
    this.http.get<ContactConfig>('/api/public/contact/config').subscribe({
      next: config => {
        if (this.disposed) return;
        this.config = config;
        this.view.detectChanges();
        if (config.enabled && config.siteKey) this.loadCaptcha(config.siteKey);
      },
      error: () => { this.config = { enabled: false, siteKey: null }; this.view.markForCheck(); },
    });
  }

  ngOnDestroy(): void {
    this.disposed = true;
    if (this.widgetId !== undefined) this.captcha?.reset(this.widgetId);
  }

  protected submit(form: NgForm): void {
    if (!this.config?.enabled || !this.captchaToken || form.invalid || this.busy) return;
    this.busy = true;
    this.feedback = '';
    this.error = '';
    this.http.post('/api/public/contact', { ...this.fields, recaptchaToken: this.captchaToken }).subscribe({
      next: () => {
        this.busy = false;
        this.feedback = 'Thanks — your enquiry has been received. We will get back to you by email.';
        form.resetForm();
        this.captchaToken = '';
        if (this.widgetId !== undefined) this.captcha?.reset(this.widgetId);
        this.view.markForCheck();
      },
      error: (response: HttpErrorResponse) => {
        this.busy = false;
        this.error = response.status === 429
          ? 'Too many attempts. Please try again in a few minutes.'
          : response.error?.message ?? 'We could not submit your enquiry. Please try again.';
        this.captchaToken = '';
        if (this.widgetId !== undefined) this.captcha?.reset(this.widgetId);
        this.view.markForCheck();
      },
    });
  }

  private loadCaptcha(siteKey: string): void {
    const show = () => {
      if (this.disposed) return;
      this.captcha = (window as Window & { grecaptcha?: { enterprise?: RecaptchaApi } }).grecaptcha?.enterprise;
      const target = document.getElementById('sarthico-contact-captcha');
      if (!this.captcha || !target || this.widgetId !== undefined) return;
      this.widgetId = this.captcha.render(target, {
        sitekey: siteKey,
        theme: 'light',
        callback: (token: string) => { this.captchaToken = token; this.view.markForCheck(); },
        'expired-callback': () => { this.captchaToken = ''; this.view.markForCheck(); },
        'error-callback': () => { this.captchaToken = ''; this.view.markForCheck(); },
      });
    };
    const existing = document.querySelector<HTMLScriptElement>('script[data-sarthico-recaptcha]');
    if (existing) { if ((window as Window & { grecaptcha?: { enterprise?: RecaptchaApi } }).grecaptcha?.enterprise) show(); else existing.addEventListener('load', show, { once: true }); return; }
    const script = document.createElement('script');
    script.src = 'https://www.google.com/recaptcha/enterprise.js?render=explicit';
    script.async = true;
    script.defer = true;
    script.dataset['sarthicoRecaptcha'] = 'true';
    script.addEventListener('load', show, { once: true });
    document.head.append(script);
  }
}
