import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideTestTransloco } from '../../testing/test-transloco';
import { AuthService } from '../auth/auth.service';
import { NoticeService } from '../ui/notice.service';
import { LocaleService } from './locale.service';

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

describe('LocaleService', () => {
  let locale: LocaleService;
  let auth: AuthService;
  let notices: NoticeService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), ...provideTestTransloco()],
    });

    locale = TestBed.inject(LocaleService);
    auth = TestBed.inject(AuthService);
    notices = TestBed.inject(NoticeService);
    http = TestBed.inject(HttpTestingController);

    sessionStorage.clear();
    localStorage.clear();
    document.documentElement.removeAttribute('dir');
    document.documentElement.removeAttribute('lang');
  });

  afterEach(() => http.verify());

  it('ar sets <html lang="ar" dir="rtl">, en flips both back — no reload', () => {
    locale.use('ar');
    expect(document.documentElement.getAttribute('lang')).toBe('ar');
    expect(document.documentElement.getAttribute('dir')).toBe('rtl');

    locale.use('en');
    expect(document.documentElement.getAttribute('lang')).toBe('en');
    expect(document.documentElement.getAttribute('dir')).toBe('ltr');
  });

  it('an anonymous reload keeps the last chosen language; the session snapshot still wins', () => {
    locale.use('ar');

    // No user after a reload: init gets nothing and must not snap back to English.
    locale.init(undefined);
    expect(locale.locale()).toBe('ar');
    expect(document.documentElement.getAttribute('dir')).toBe('rtl');

    // The server snapshot always outranks the browser's last choice.
    locale.init('en');
    expect(locale.locale()).toBe('en');
    expect(document.documentElement.getAttribute('dir')).toBe('ltr');
  });

  it('a language picked on the login page follows the user in and is saved to the account', () => {
    locale.toggle(); // signed out: applies immediately, nothing to PATCH yet
    expect(locale.locale()).toBe('ar');
    http.expectNone('/api/v1/tenants/t1/users/u1/locale');

    auth.apply(SESSION);
    locale.adoptLoginLocale('en'); // the account says en; this tab just chose ar

    const request = http.expectOne('/api/v1/tenants/t1/users/u1/locale');
    expect(request.request.method).toBe('PATCH');
    expect(request.request.body).toEqual({ locale: 'ar' });
    expect(locale.locale()).toBe('ar');
    expect(document.documentElement.getAttribute('dir')).toBe('rtl');

    request.flush({ userId: 'u1', tenantId: 't1', email: 'x', role: 'admin', locale: 'ar' });
  });

  it('toggles and PATCHes the exact locale door with {locale}', () => {
    auth.apply(SESSION);
    locale.init('en');

    locale.toggle();

    const request = http.expectOne('/api/v1/tenants/t1/users/u1/locale');
    expect(request.request.method).toBe('PATCH');
    expect(request.request.body).toEqual({ locale: 'ar' });
    expect(locale.locale()).toBe('ar');
    expect(document.documentElement.getAttribute('dir')).toBe('rtl');

    request.flush({ userId: 'u1', tenantId: 't1', email: 'x', role: 'admin', locale: 'ar' });
  });

  it('a failing PATCH keeps the chosen language and surfaces a localized error', () => {
    auth.apply(SESSION);
    locale.init('en');

    locale.toggle();

    const request = http.expectOne('/api/v1/tenants/t1/users/u1/locale');
    request.flush({ code: 'identity:forbidden' }, { status: 403, statusText: 'Forbidden' });

    expect(locale.locale()).toBe('ar');
    expect(document.documentElement.getAttribute('dir')).toBe('rtl');
    expect(notices.notices().map((notice) => notice.key)).toContain('locale.persistFailed');
  });
});
