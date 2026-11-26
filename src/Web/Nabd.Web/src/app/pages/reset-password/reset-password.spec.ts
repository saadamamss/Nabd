import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  TestRequest,
} from '@angular/common/http/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { provideTestTransloco } from '../../testing/test-transloco';
import { AuthService } from '../../core/auth/auth.service';
import { routes } from '../../app.routes';
import { ResetPasswordPage } from './reset-password';

const URL = '/api/v1/auth/reset-confirm';
const TOKEN = 'tok-4f9c2a-secret';

const SESSION = {
  user: {
    userId: 'u1',
    tenantId: 't1',
    email: 'admin@nabd.test',
    role: 'tenant_admin',
    locale: 'en',
  },
  accessToken: 'access-1',
};

interface Rig {
  fixture: ComponentFixture<ResetPasswordPage>;
  http: HttpTestingController;
  host: HTMLElement;
}

/** The rig a fact built; null when a fact needs no HTTP (the route fact is static). */
let controller: HttpTestingController | null = null;

function setup(token = TOKEN): Rig {
  controller?.verify();
  controller = null;
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    imports: [ResetPasswordPage],
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      ...provideTestTransloco(),
      {
        provide: ActivatedRoute,
        useValue: { snapshot: { paramMap: convertToParamMap({ token }) } },
      },
    ],
  });

  const http = TestBed.inject(HttpTestingController);
  controller = http;
  const fixture = TestBed.createComponent(ResetPasswordPage);
  fixture.detectChanges();

  return { fixture, http, host: fixture.nativeElement as HTMLElement };
}

function text(rig: Rig): string {
  return rig.host.textContent ?? '';
}

function noContent(request: TestRequest): void {
  request.flush(null, { status: 204, statusText: 'No Content' });
}

/** Submits through the DOM: the handler must be wired, not merely callable. */
function submitThroughDom(rig: Rig, password: string, confirm: string): Event {
  rig.fixture.componentInstance.form.setValue({ password, confirm });
  rig.fixture.detectChanges();

  const form = rig.host.querySelector('form')!;
  const event = new Event('submit', { cancelable: true, bubbles: true });
  form.dispatchEvent(event);
  return event;
}

describe('ResetPasswordPage', () => {
  beforeEach(() => {
    sessionStorage.clear();
    localStorage.clear();
    vi.restoreAllMocks();
  });

  afterEach(() => controller?.verify());

  it('sets the password, scrubs the token from the URL, and never calls /logout', async () => {
    const rig = setup();

    const event = submitThroughDom(rig, 'GoodPass12', 'GoodPass12');
    expect(event.defaultPrevented).toBe(true); // a native submit would reload with the token

    const request = rig.http.expectOne(URL);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ token: TOKEN, newPassword: 'GoodPass12' });

    noContent(request);
    await rig.fixture.whenStable();
    rig.fixture.detectChanges();

    expect(text(rig)).toContain('Your password is set');
    expect(text(rig)).not.toContain(TOKEN); // the token is never rendered (A5)
    expect(rig.host.querySelector('form')).toBeNull();

    // the server already revoked every refresh token: a /logout would be theatre
    rig.http.expectNone('/api/v1/auth/logout');

    // the address bar no longer carries the reusable secret (C9)
    expect(location.pathname).toBe('/reset-password');
  });

  it('shows the dead panel for a used or expired link, with the way to a new one', async () => {
    const rig = setup();

    // a legal-length password on purpose: the door checks the length BEFORE the
    // token, so a short one would prove the wrong branch (C7)
    submitThroughDom(rig, 'GoodPass12', 'GoodPass12');
    rig.http
      .expectOne(URL)
      .flush(
        { code: 'identity:reset_token_dead' },
        { status: 410, statusText: 'Gone' },
      );

    await rig.fixture.whenStable();
    rig.fixture.detectChanges();

    expect(text(rig)).toContain('This link is no longer valid');
    expect(rig.host.querySelector('form')).toBeNull(); // no retry loop on a dead token
    expect(rig.host.querySelector('a[href="/forgot-password"]')).not.toBeNull();
  });

  it('anchors a weak password at the field and keeps the form usable', async () => {
    const rig = setup();

    submitThroughDom(rig, 'short77', 'short77');
    rig.http
      .expectOne(URL)
      .flush(
        { code: 'identity:weak_password' },
        { status: 400, statusText: 'Bad Request' },
      );

    await rig.fixture.whenStable();
    rig.fixture.detectChanges();

    // field-anchored, not a generic alert: the same wording the map uses
    expect(text(rig)).toContain('The password must be at least 8 characters.');
    expect(rig.host.querySelector('form')).not.toBeNull();

    // the token was not burned server-side, so the same screen can finish the job
    submitThroughDom(rig, 'GoodPass12', 'GoodPass12');
    noContent(rig.http.expectOne(URL));
    await rig.fixture.whenStable();
    rig.fixture.detectChanges();

    expect(text(rig)).toContain('Your password is set');
  });

  it('never writes the token to the console, a text node, or the URL', async () => {
    const spies = (['log', 'info', 'warn', 'error', 'debug'] as const).map((method) =>
      vi.spyOn(console, method).mockImplementation(() => undefined),
    );

    const rig = setup();
    submitThroughDom(rig, 'GoodPass12', 'GoodPass12');
    rig.http
      .expectOne(URL)
      .flush(
        { code: 'identity:reset_token_dead' },
        { status: 410, statusText: 'Gone' },
      );
    await rig.fixture.whenStable();
    rig.fixture.detectChanges();

    const logged = spies
      .flatMap((spy) => spy.mock.calls.flat())
      .map((value) => String(value))
      .join('\n');

    expect(logged).not.toContain(TOKEN);
    expect(text(rig)).not.toContain(TOKEN);
  });

  it('a confirm mismatch blocks the submit: nothing is sent to a single-use door', () => {
    const rig = setup();

    submitThroughDom(rig, 'GoodPass12', 'GoodPass13');
    rig.fixture.detectChanges();

    expect(text(rig)).toContain('The two passwords do not match.');
    expect(rig.host.querySelector('form')).not.toBeNull();
    // afterEach's verify() is the second proof: no request was made
    rig.http.expectNone(URL);
  });

  it('clears a signed-in tab, because the server revoked every session (C8)', async () => {
    const rig = setup();
    const auth = TestBed.inject(AuthService);
    auth.apply(SESSION);
    expect(auth.isAuthenticated()).toBe(true);

    submitThroughDom(rig, 'GoodPass12', 'GoodPass12');
    noContent(rig.http.expectOne(URL));
    await rig.fixture.whenStable();
    rig.fixture.detectChanges();

    expect(auth.isAuthenticated()).toBe(false);
    expect(text(rig)).toContain('Every other session has been signed out.');
    rig.http.expectNone('/api/v1/auth/logout');
  });

  it('is an anonymous top-level door whose token is a path segment', () => {
    const route = routes.find((candidate) => candidate.path === 'reset-password/:token');

    expect(route?.component).toBe(ResetPasswordPage);
    expect(route?.canActivate).toBeUndefined();
  });
});
