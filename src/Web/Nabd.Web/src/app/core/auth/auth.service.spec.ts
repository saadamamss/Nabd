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
  refreshToken: 'refresh-1',
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
  });

  afterEach(() => http.verify());

  it('stores the refresh token in sessionStorage and rotates it on refresh', () => {
    auth.apply(SESSION);
    expect(sessionStorage.getItem('nabd.refresh')).toBe('refresh-1');
    expect(auth.accessToken()).toBe('access-1');

    auth.refresh().subscribe();

    const request = http.expectOne('/api/v1/auth/refresh');
    expect(request.request.body).toEqual({ refreshToken: 'refresh-1' });
    request.flush({ ...SESSION, accessToken: 'access-2', refreshToken: 'refresh-2' });

    expect(sessionStorage.getItem('nabd.refresh')).toBe('refresh-2');
    expect(auth.accessToken()).toBe('access-2');
  });

  it('logout posts {refreshToken}, and local state clears before the server answers', () => {
    auth.apply(SESSION);

    let completed = false;
    auth.logout().subscribe({ complete: () => (completed = true) });

    expect(auth.user()).toBeNull();
    expect(sessionStorage.getItem('nabd.refresh')).toBeNull();

    const request = http.expectOne('/api/v1/auth/logout');
    expect(request.request.body).toEqual({ refreshToken: 'refresh-1' });

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
    expect(sessionStorage.getItem('nabd.refresh')).toBeNull();
  });

  it('restore() refreshes silently when a refresh token survived the reload', async () => {
    sessionStorage.setItem('nabd.refresh', 'refresh-1');

    const restored = auth.restore();
    http
      .expectOne('/api/v1/auth/refresh')
      .flush({ ...SESSION, accessToken: 'access-2', refreshToken: 'refresh-2' });
    await restored;

    expect(auth.accessToken()).toBe('access-2');
    expect(sessionStorage.getItem('nabd.refresh')).toBe('refresh-2');
  });
});
