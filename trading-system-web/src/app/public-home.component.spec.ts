import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { PublicHomeComponent } from './public-home.component';

describe('Public homepage', () => {
  beforeEach(() => TestBed.configureTestingModule({
    imports: [PublicHomeComponent],
    providers: [provideHttpClient(), provideHttpClientTesting()],
  }));

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
});
