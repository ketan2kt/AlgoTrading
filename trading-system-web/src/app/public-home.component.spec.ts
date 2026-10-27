import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { PublicHomeComponent } from './public-home.component';

describe('Public homepage', () => {
  beforeEach(() => TestBed.configureTestingModule({
    imports: [PublicHomeComponent],
    providers: [provideHttpClient(), provideHttpClientTesting()],
  }));

  afterEach(() => {
    document.querySelector('script[data-sarthico-recaptcha]')?.remove();
    delete (window as Window & { grecaptcha?: unknown }).grecaptcha;
  });

  it('presents all services and keeps contact submission disabled until verified delivery is configured', () => {
    const fixture = TestBed.createComponent(PublicHomeComponent);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/public/contact/config')
      .flush({ enabled: false, siteKey: null });
    fixture.detectChanges();

    const content = fixture.nativeElement.textContent as string;
    expect(content).toContain('Web development');
    expect(content).toContain('Application development');
    expect(content).toContain('AI applications');
    expect(content).toContain('Our contact form is being prepared');
    const submit = fixture.nativeElement.querySelector('button[type="submit"]') as HTMLButtonElement;
    expect(submit.disabled).toBe(true);
    TestBed.inject(HttpTestingController).verify();
  });

  it('shows an actionable error if the CAPTCHA script fails to load', () => {
    const fixture = TestBed.createComponent(PublicHomeComponent);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/public/contact/config')
      .flush({ enabled: true, siteKey: 'test-site-key' });
    fixture.detectChanges();

    const script = document.querySelector<HTMLScriptElement>('script[data-sarthico-recaptcha]');
    expect(script).not.toBeNull();
    script?.dispatchEvent(new Event('error'));
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Spam protection could not load');
    expect((fixture.nativeElement.querySelector('button[type="submit"]') as HTMLButtonElement).disabled).toBe(true);
    TestBed.inject(HttpTestingController).verify();
    fixture.destroy();
  });

  it('loads a v3 key and enables submission after the score API is ready', () => {
    const fixture = TestBed.createComponent(PublicHomeComponent);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/public/contact/config')
      .flush({ enabled: true, siteKey: 'v3-site-key' });
    fixture.detectChanges();

    const script = document.querySelector<HTMLScriptElement>('script[data-sarthico-recaptcha]');
    expect(script?.src).toBe('https://www.google.com/recaptcha/enterprise.js?render=v3-site-key');
    let markReady: (() => void) | undefined;
    (window as Window & { grecaptcha?: unknown }).grecaptcha = {
      enterprise: {
        ready: (callback: () => void) => { markReady = callback; },
        execute: async () => 'score-token',
      },
    };
    script?.dispatchEvent(new Event('load'));
    fixture.detectChanges();
    expect((fixture.nativeElement.querySelector('button[type="submit"]') as HTMLButtonElement).disabled).toBe(true);

    markReady?.();
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Spam protection runs automatically when you send');
    TestBed.inject(HttpTestingController).verify();
    fixture.destroy();
  });
});
