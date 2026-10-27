import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AdminPanelComponent } from './admin-panel.component';

describe('Admin panel', () => {
  beforeEach(() => TestBed.configureTestingModule({
    imports: [AdminPanelComponent],
    providers: [provideHttpClient(), provideHttpClientTesting()],
  }));

  it('leaves the session-check screen when the API returns an anonymous response', async () => {
    const fixture = TestBed.createComponent(AdminPanelComponent);
    fixture.autoDetectChanges();

    TestBed.inject(HttpTestingController).expectOne('/api/auth/me')
      .flush(null, { status: 401, statusText: 'Unauthorized' });
    await fixture.whenStable();

    expect(fixture.nativeElement.textContent).toContain('Sign in to Sarthico');
    TestBed.inject(HttpTestingController).verify();
    fixture.destroy();
  });
});
