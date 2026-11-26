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

  it('attaches the bearer, refreshes once on 401, retries once with the new token', () => {
    auth.apply(SESSION);
    let received: unknown = null;

    client.get('/api/v1/assets').subscribe((response) => (received = response));

    const first = http.expectOne('/api/v1/assets');
    expect(first.request.headers.get('Authorization')).toBe('Bearer access-1');
    first.flush({ code: 'expired' }, { status: 401, statusText: 'Unauthorized' });

    const refresh = http.expectOne('/api/v1/auth/refresh');
    expect(refresh.request.body).toBeNull(); // the cookie identifies the session
    expect(refresh.request.withCredentials).toBe(true);
    refresh.flush({ ...SESSION, accessToken: 'access-2' });

    const retry = http.expectOne('/api/v1/assets');
    expect(retry.request.headers.get('Authorization')).toBe('Bearer access-2');
    retry.flush({ ok: true });

    expect(received).toEqual({ ok: true });
    expect(auth.accessToken()).toBe('access-2');
    expect(sessionStorage.length).toBe(0); // no token was ever written here
  });

  it('a second 401 is terminal: session cleared, routed to login with returnUrl', () => {
    auth.apply(SESSION);
    client.get('/api/v1/assets').subscribe({ error: () => undefined });

    http.expectOne('/api/v1/assets').flush({}, { status: 401, statusText: 'Unauthorized' });
    http.expectOne('/api/v1/auth/refresh').flush({ ...SESSION, accessToken: 'access-2' });
    http.expectOne('/api/v1/assets').flush({}, { status: 401, statusText: 'Unauthorized' });

    expect(auth.user()).toBeNull();
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

  it('a wrong current password is the door’s answer, not an expired session (C1)', () => {
    auth.apply(SESSION);
    let failure: unknown = null;

    client
      .patch('/api/v1/auth/t1/users/u1/password', {
        currentPassword: 'WrongHorse1',
        newPassword: 'GoodPass12',
      })
      .subscribe({ error: (error: unknown) => (failure = error) });

    http
      .expectOne('/api/v1/auth/t1/users/u1/password')
      .flush({ code: 'identity:invalid_credentials' }, { status: 401, statusText: 'Unauthorized' });

    // no /auth/refresh, no retry, no navigation: verify() proves the absence
    expect((failure as { status?: number }).status).toBe(401);
    expect(auth.isAuthenticated()).toBe(true);
    expect(auth.accessToken()).toBe('access-1');
    expect(router.navigate).not.toHaveBeenCalled();
    expect(notices.notices().length).toBe(0);
  });

  it('a 401 on the change door WITHOUT that code is still an expired token', () => {
    auth.apply(SESSION);
    let completed = false;

    client
      .patch('/api/v1/auth/t1/users/u1/password', {
        currentPassword: 'CorrectHorse1',
        newPassword: 'GoodPass12',
      })
      .subscribe({ complete: () => (completed = true) });

    http
      .expectOne('/api/v1/auth/t1/users/u1/password')
      .flush({}, { status: 401, statusText: 'Unauthorized' });

    http.expectOne('/api/v1/auth/refresh').flush({ ...SESSION, accessToken: 'access-2' });

    const retry = http.expectOne('/api/v1/auth/t1/users/u1/password');
    expect(retry.request.headers.get('Authorization')).toBe('Bearer access-2');
    retry.flush(null, { status: 204, statusText: 'No Content' });

    expect(completed).toBe(true);
    expect(auth.accessToken()).toBe('access-2');
    expect(router.navigate).not.toHaveBeenCalled();
  });

  it('never attaches a bearer to the pre-auth doors', () => {
    auth.apply(SESSION);

    client.post('/api/v1/auth/login', { email: 'a', password: 'b' }).subscribe();

    const request = http.expectOne('/api/v1/auth/login');
    expect(request.request.headers.has('Authorization')).toBe(false);
    request.flush(SESSION);
  });
});
