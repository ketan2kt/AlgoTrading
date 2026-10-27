import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectorRef, Component, inject, OnDestroy, OnInit } from '@angular/core';
import { FormsModule, NgForm } from '@angular/forms';

interface ContactConfig { enabled: boolean; siteKey: string | null }
interface RecaptchaApi {
  ready(callback: () => void): void;
  execute(siteKey: string, options: { action: string }): Promise<string>;
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
  private captchaTimeout: ReturnType<typeof setTimeout> | undefined;
  private disposed = false;
  protected config: ContactConfig | null = null;
  protected busy = false;
  protected feedback = '';
  protected error = '';
  protected captchaError = '';
  protected captchaLoading = false;
  protected captchaReady = false;
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
    if (this.captchaTimeout !== undefined) clearTimeout(this.captchaTimeout);
  }

  protected async submit(form: NgForm): Promise<void> {
    if (!this.config?.enabled || !this.config.siteKey || !this.captchaReady || form.invalid || this.busy || !this.captcha) return;
    this.busy = true;
    this.feedback = '';
    this.error = '';
    this.captchaError = '';
    this.view.markForCheck();

    let token: string;
    try {
      token = await this.captcha.execute(this.config.siteKey, { action: 'CONTACT_SUBMIT' });
      if (!token) throw new Error('Empty reCAPTCHA token');
    } catch {
      if (this.disposed) return;
      this.busy = false;
      this.captchaError = 'Spam protection could not run. Please refresh the page and try again.';
      this.view.markForCheck();
      return;
    }
    if (this.disposed) return;

    this.http.post('/api/public/contact', { ...this.fields, recaptchaToken: token }).subscribe({
      next: () => {
        this.busy = false;
        this.feedback = 'Thanks — your enquiry has been received. We will get back to you by email.';
        form.resetForm();
        this.view.markForCheck();
      },
      error: (response: HttpErrorResponse) => {
        this.busy = false;
        this.error = response.status === 429
          ? 'Too many attempts. Please try again in a few minutes.'
          : response.error?.message ?? 'We could not submit your enquiry. Please try again.';
        this.view.markForCheck();
      },
    });
  }

  private loadCaptcha(siteKey: string): void {
    this.captchaLoading = true;
    this.captchaError = '';
    this.view.markForCheck();
    const fail = () => {
      if (this.disposed || this.captchaReady) return;
      this.captchaLoading = false;
      this.captchaError = 'Spam protection could not load. Please refresh the page and try again.';
      if (this.captchaTimeout !== undefined) clearTimeout(this.captchaTimeout);
      this.view.markForCheck();
    };
    const prepare = () => {
      if (this.disposed) return;
      const api = (window as Window & { grecaptcha?: { enterprise?: RecaptchaApi } }).grecaptcha?.enterprise;
      if (!api) { fail(); return; }
      try {
        api.ready(() => {
          if (this.disposed) return;
          this.captcha = api;
          this.captchaReady = true;
          this.captchaLoading = false;
          if (this.captchaTimeout !== undefined) clearTimeout(this.captchaTimeout);
          this.view.markForCheck();
        });
      } catch {
        fail();
      }
    };
    const existing = document.querySelector<HTMLScriptElement>('script[data-sarthico-recaptcha]');
    if (existing) {
      if (existing.dataset['loaded'] === 'true' || (window as Window & { grecaptcha?: { enterprise?: RecaptchaApi } }).grecaptcha?.enterprise) prepare();
      else if (existing.dataset['failed'] === 'true') fail();
      else {
        existing.addEventListener('load', () => { existing.dataset['loaded'] = 'true'; prepare(); }, { once: true });
        existing.addEventListener('error', fail, { once: true });
      }
      this.startCaptchaTimeout(fail);
      return;
    }
    const script = document.createElement('script');
    script.src = `https://www.google.com/recaptcha/enterprise.js?render=${encodeURIComponent(siteKey)}`;
    script.async = true;
    script.defer = true;
    script.dataset['sarthicoRecaptcha'] = 'true';
    script.addEventListener('load', () => { script.dataset['loaded'] = 'true'; prepare(); }, { once: true });
    script.addEventListener('error', () => { script.dataset['failed'] = 'true'; fail(); }, { once: true });
    document.head.append(script);
    this.startCaptchaTimeout(fail);
  }

  private startCaptchaTimeout(fail: () => void): void {
    this.captchaTimeout = setTimeout(fail, 12_000);
  }
}
