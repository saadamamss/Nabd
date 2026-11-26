import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AuthService } from './auth.service';

const SESSION = {
  user: {
    userId: 'u1',
    tenantId: 't1',
    email: 'admin@nabd.test',
    role: 'admin',
    locale: 'ar',
  },
  accessToken: 'access-1',
};

describe('AuthService', () => {
  let auth: AuthService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    auth = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
    sessionStorage.clear();
    localStorage.clear();
  });

  afterEach(() => http.verify());

  it('keeps the access token in memory only — no token lands in storage', () => {
    auth.apply(SESSION);

    expect(auth.accessToken()).toBe('access-1');
    expect(sessionStorage.getItem('nabd.refresh')).toBeNull();
    expect(localStorage.getItem('nabd.refresh')).toBeNull();
    expect(sessionStorage.length).toBe(0);
    expect(localStorage.length).toBe(0);
  });

  it('refreshes with no body: the browser presents the HttpOnly cookie', () => {
    auth.apply(SESSION);

    auth.refresh().subscribe();

    const request = http.expectOne('/api/v1/auth/refresh');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toBeNull();
    expect(request.request.withCredentials).toBe(true);

    request.flush({ ...SESSION, accessToken: 'access-2' });

    expect(auth.accessToken()).toBe('access-2');
    expect(sessionStorage.getItem('nabd.refresh')).toBeNull();
  });

  it('logout clears local state before the server answers and sends no token', () => {
    auth.apply(SESSION);

    let completed = false;
    auth.logout().subscribe({ complete: () => (completed = true) });

    expect(auth.user()).toBeNull();

    const request = http.expectOne('/api/v1/auth/logout');
    expect(request.request.body).toBeNull();
    expect(request.request.withCredentials).toBe(true);

    request.flush({ code: 'identity:something' }, { status: 400, statusText: 'Bad Request' });

    expect(completed).toBe(true);
    expect(auth.user()).toBeNull();
  });

  it('a failed logout still clears the session (never traps the user)', () => {
    auth.apply(SESSION);

    auth.logout().subscribe();

    http
      .expectOne('/api/v1/auth/logout')
      .error(new ProgressEvent('network-error'), { status: 0, statusText: 'Unknown Error' });

    expect(auth.user()).toBeNull();
  });

  it('a failed logout cannot resurrect the session on the next reload', async () => {
    auth.apply(SESSION);

    auth.logout().subscribe();
    http
      .expectOne('/api/v1/auth/logout')
      .error(new ProgressEvent('network-error'), { status: 0, statusText: 'Unknown Error' });

    const restored = auth.restore();

    // Boot finishes the kill instead of calling /refresh, so no session returns.
    http.expectOne('/api/v1/auth/logout').flush(null, { status: 204, statusText: 'No Content' });
    await restored;

    expect(auth.accessToken()).toBeNull();
  });

  it('changePassword calls the self-only door with the snapshot ids (C11)', () => {
    auth.apply(SESSION);

    auth.changePassword('CorrectHorse1', 'GoodPass12').subscribe();

    const request = http.expectOne('/api/v1/auth/t1/users/u1/password');
    expect(request.request.method).toBe('PATCH');
    expect(request.request.body).toEqual({
      currentPassword: 'CorrectHorse1',
      newPassword: 'GoodPass12',
    });

    request.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('changePassword without a session sends nothing (the page is self-only)', () => {
    let failure: unknown = null;

    auth.changePassword('CorrectHorse1', 'GoodPass12').subscribe({
      error: (error: unknown) => (failure = error),
    });

    expect(failure).toBeInstanceOf(Error);
    // afterEach's verify() is the second proof: no request was made
  });

  it('restore() always asks the cookie for a session, because it cannot read it', async () => {
    const restored = auth.restore();

    const request = http.expectOne('/api/v1/auth/refresh');
    expect(request.request.withCredentials).toBe(true);
    request.flush({ ...SESSION, accessToken: 'access-2' });
    await restored;

    expect(auth.accessToken()).toBe('access-2');
  });

  it('restore() leaves the app signed out when the cookie is dead', async () => {
    const restored = auth.restore();

    http
      .expectOne('/api/v1/auth/refresh')
      .flush({ code: 'identity:invalid_credentials' }, { status: 401, statusText: 'Unauthorized' });
    await restored;

    expect(auth.accessToken()).toBeNull();
    expect(auth.user()).toBeNull();
  });
});
