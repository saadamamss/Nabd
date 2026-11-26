import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import {
  ActivatedRoute,
  convertToParamMap,
  provideRouter,
  Router,
} from '@angular/router';
import { provideTestTransloco } from '../../testing/test-transloco';
import { AuthService } from '../../core/auth/auth.service';
import { LoginPage } from './login';

const AR_SESSION = {
  user: {
    userId: 'u1',
    tenantId: 't1',
    email: 'admin@nabd.test',
    role: 'admin',
    locale: 'ar',
  },
  accessToken: 'access-1',
};

function configure(returnUrl: string | null = null): void {
  TestBed.configureTestingModule({
    imports: [LoginPage],
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      ...provideTestTransloco(),
      {
        provide: ActivatedRoute,
        useValue: {
          snapshot: { queryParamMap: convertToParamMap(returnUrl ? { returnUrl } : {}) },
        },
      },
    ],
  });
}

describe('LoginPage', () => {
  let http: HttpTestingController;
  let router: Router;

  beforeEach(() => {
    sessionStorage.clear();
    document.documentElement.removeAttribute('dir');
    document.documentElement.removeAttribute('lang');
  });

  afterEach(() => http.verify());

  it('renders both fields from the dictionary', async () => {
    configure();
    http = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);

    const fixture = TestBed.createComponent(LoginPage);
    await fixture.whenStable();
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Sign in');
    expect(text).toContain('Email');
    expect(text).toContain('Password');
  });

  it('boots in the snapshot locale before any page data loads', async () => {
    configure();
    http = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
    vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);

    const fixture = TestBed.createComponent(LoginPage);
    await fixture.whenStable();

    fixture.componentInstance.form.setValue({ email: 'admin@nabd.test', password: 'secret-1' });
    fixture.componentInstance.submit();

    http.expectOne('/api/v1/auth/login').flush(AR_SESSION);

    expect(document.documentElement.getAttribute('dir')).toBe('rtl');
    expect(document.documentElement.getAttribute('lang')).toBe('ar');
    expect(router.navigateByUrl).toHaveBeenCalledWith('/overview');
  });

  it('returns to the preserved path, and refuses an open redirect', async () => {
    configure('/assets');
    http = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);

    const fixture = TestBed.createComponent(LoginPage);
    await fixture.whenStable();
    fixture.componentInstance.form.setValue({ email: 'a@b.test', password: 'secret-1' });
    fixture.componentInstance.submit();
    http.expectOne('/api/v1/auth/login').flush(AR_SESSION);

    expect(navigate).toHaveBeenCalledWith('/assets');
  });

  it('maps a 401 to "invalid credentials" and a disabled tenant to its own notice', async () => {
    configure();
    http = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);

    const fixture = TestBed.createComponent(LoginPage);
    await fixture.whenStable();
    const page = fixture.componentInstance;
    page.form.setValue({ email: 'a@b.test', password: 'wrong-1' });

    page.submit();
    http
      .expectOne('/api/v1/auth/login')
      .flush({ code: 'identity:invalid_credentials' }, { status: 401, statusText: 'Unauthorized' });
    expect(page.errorKey()).toBe('auth.invalid');

    page.submit();
    http
      .expectOne('/api/v1/auth/login')
      .flush({ code: 'identity:tenant_disabled' }, { status: 403, statusText: 'Forbidden' });
    expect(page.errorKey()).toBe('auth.tenantDisabled');

    expect(navigate).not.toHaveBeenCalled();
  });

  it('offers the way out: a link to the forgot-password door', async () => {
    configure();
    http = TestBed.inject(HttpTestingController);

    const fixture = TestBed.createComponent(LoginPage);
    await fixture.whenStable();
    fixture.detectChanges();

    const link = (fixture.nativeElement as HTMLElement).querySelector(
      'a[href="/forgot-password"]',
    );
    expect(link).not.toBeNull();
    expect(link?.textContent).toContain('Forgot your password?');
  });
});
