import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { provideTestTransloco } from '../../testing/test-transloco';
import { NoticeService } from '../ui/notice.service';
import { authInterceptor } from './auth.interceptor';
import { AuthService } from './auth.service';

const SESSION = {
  user: {
    userId: 'u1',
    tenantId: 't1',
    email: 'admin@nabd.test',
    role: 'admin',
    locale: 'en',
  },
  accessToken: 'access-1',
  refreshToken: 'refresh-1',
};

describe('authInterceptor', () => {
  let client: HttpClient;
  let auth: AuthService;
  let http: HttpTestingController;
  let router: Router;
  let notices: NoticeService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        provideRouter([]),
        ...provideTestTransloco(),
      ],
    });

    client = TestBed.inject(HttpClient);
    auth = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
    notices = TestBed.inject(NoticeService);

    sessionStorage.clear();
    vi.spyOn(router, 'navigate').mockResolvedValue(true);
  });

  afterEach(() => http.verify());

  it('attaches the bearer, refreshes once on 401, stores the rotated token, retries once', () => {
    auth.apply(SESSION);
    let received: unknown = null;

    client.get('/api/v1/assets').subscribe((response) => (received = response));

    const first = http.expectOne('/api/v1/assets');
    expect(first.request.headers.get('Authorization')).toBe('Bearer access-1');
    first.flush({ code: 'expired' }, { status: 401, statusText: 'Unauthorized' });

    const refresh = http.expectOne('/api/v1/auth/refresh');
    expect(refresh.request.body).toEqual({ refreshToken: 'refresh-1' });
    refresh.flush({ ...SESSION, accessToken: 'access-2', refreshToken: 'refresh-2' });

    const retry = http.expectOne('/api/v1/assets');
    expect(retry.request.headers.get('Authorization')).toBe('Bearer access-2');
    retry.flush({ ok: true });

    expect(received).toEqual({ ok: true });
    expect(auth.accessToken()).toBe('access-2');
    expect(sessionStorage.getItem('nabd.refresh')).toBe('refresh-2');
  });

  it('a second 401 is terminal: session cleared, routed to login with returnUrl', () => {
    auth.apply(SESSION);
    client.get('/api/v1/assets').subscribe({ error: () => undefined });

    http.expectOne('/api/v1/assets').flush({}, { status: 401, statusText: 'Unauthorized' });
    http
      .expectOne('/api/v1/auth/refresh')
      .flush({ ...SESSION, accessToken: 'access-2', refreshToken: 'refresh-2' });
    http.expectOne('/api/v1/assets').flush({}, { status: 401, statusText: 'Unauthorized' });

    expect(auth.user()).toBeNull();
    expect(sessionStorage.getItem('nabd.refresh')).toBeNull();
    expect(router.navigate).toHaveBeenCalledWith(['/login'], {
      queryParams: { returnUrl: '/' },
    });
    expect(notices.notices().map((notice) => notice.key)).toContain('auth.sessionExpired');
  });

  it('a refresh answering 403 tenant_disabled clears the session without retrying', () => {
    auth.apply(SESSION);
    client.get('/api/v1/assets').subscribe({ error: () => undefined });

    http.expectOne('/api/v1/assets').flush({}, { status: 401, statusText: 'Unauthorized' });
    http.expectOne('/api/v1/auth/refresh').flush(
      { code: 'identity:tenant_disabled' },
      { status: 403, statusText: 'Forbidden' },
    );

    // http.verify() in afterEach proves no retry and no second refresh.
    expect(auth.user()).toBeNull();
    expect(router.navigate).toHaveBeenCalledWith(['/login'], { queryParams: {} });
    expect(notices.notices().map((notice) => notice.key)).toContain('auth.tenantDisabled');
  });

  it('never attaches a bearer to the pre-auth doors', () => {
    auth.apply(SESSION);

    client.post('/api/v1/auth/login', { email: 'a', password: 'b' }).subscribe();

    const request = http.expectOne('/api/v1/auth/login');
    expect(request.request.headers.has('Authorization')).toBe(false);
    request.flush(SESSION);
  });
});
