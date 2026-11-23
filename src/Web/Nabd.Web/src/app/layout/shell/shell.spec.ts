import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideTestTransloco } from '../../testing/test-transloco';
import { AuthService } from '../../core/auth/auth.service';
import { Shell } from './shell';

function session(role: string) {
  return {
    user: {
      userId: 'u1',
      tenantId: 't1',
      email: 'admin@nabd.test',
      role,
      locale: 'en',
    },
    accessToken: 'access-1',
  };
}

describe('Shell', () => {
  let auth: AuthService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [Shell],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        ...provideTestTransloco(),
      ],
    });

    auth = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
    sessionStorage.clear();
  });

  afterEach(() => http.verify());

  it('renders the tenant name fetched with the token tenant id', async () => {
    auth.apply(session('admin'));

    const fixture = TestBed.createComponent(Shell);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    http
      .expectOne('/api/v1/tenants/t1')
      .flush({ tenantId: 't1', name: 'Cold Chain Co', isActive: true });

    await fixture.whenStable();
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Cold Chain Co');
  });

  it('a failed tenant read degrades to the product name, never a blank topbar', async () => {
    auth.apply(session('admin'));

    const fixture = TestBed.createComponent(Shell);
    fixture.detectChanges();
    await fixture.whenStable();

    http
      .expectOne('/api/v1/tenants/t1')
      .flush({ code: 'identity:not_found' }, { status: 404, statusText: 'Not Found' });

    await fixture.whenStable();
    fixture.detectChanges();

    const topbar = (fixture.nativeElement as HTMLElement).querySelector('.topbar');
    expect(topbar?.textContent).toContain('Nabd');
  });

  it('shows Platform only to platform_admin', async () => {
    auth.apply(session('admin'));

    const fixture = TestBed.createComponent(Shell);
    fixture.detectChanges();
    await fixture.whenStable();
    http
      .expectOne('/api/v1/tenants/t1')
      .flush({ tenantId: 't1', name: 'Co', isActive: true });
    await fixture.whenStable();
    fixture.detectChanges();

    const links = () =>
      Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('.nav-link')).map(
        (link) => link.textContent?.trim(),
      );

    expect(links()).toContain('Overview');
    expect(links()).not.toContain('Platform');
  });
});
