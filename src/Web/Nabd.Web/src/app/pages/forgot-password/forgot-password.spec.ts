import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  TestRequest,
} from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { TranslocoService } from '@jsverse/transloco';
import { provideTestTransloco } from '../../testing/test-transloco';
import { DICTIONARIES } from '../../testing/test-dictionaries';
import { routes } from '../../app.routes';
import { Shell } from '../../layout/shell/shell';
import { ForgotPasswordPage } from './forgot-password';

const URL = '/api/v1/auth/reset-password';

/** The one sentence every address gets (A3) — the same words, whoever asks. */
const NEUTRAL =
  'If that address has an account, a link is on its way. It is valid for 15 minutes.';

interface Rig {
  fixture: ComponentFixture<ForgotPasswordPage>;
  http: HttpTestingController;
  host: HTMLElement;
}

/** The rig a fact built; null when a fact needs no HTTP (the route fact is static). */
let controller: HttpTestingController | null = null;

function setup(): Rig {
  // a fact may need a second page instance (known vs unknown address): close the
  // first rig honestly, then reset so the module can be configured again
  controller?.verify();
  controller = null;
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    imports: [ForgotPasswordPage],
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      ...provideTestTransloco(),
    ],
  });

  const http = TestBed.inject(HttpTestingController);
  controller = http;
  const fixture = TestBed.createComponent(ForgotPasswordPage);
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
function submitThroughDom(rig: Rig, email: string): Event {
  rig.fixture.componentInstance.form.setValue({ email });
  rig.fixture.detectChanges();

  const form = rig.host.querySelector('form')!;
  const event = new Event('submit', { cancelable: true, bubbles: true });
  form.dispatchEvent(event);
  return event;
}

describe('ForgotPasswordPage', () => {
  beforeEach(() => {
    controller = null;
    sessionStorage.clear();
    localStorage.clear();
    document.documentElement.removeAttribute('dir');
    document.documentElement.removeAttribute('lang');
  });

  afterEach(() => controller?.verify());

  it('sends exactly one POST with the typed address, and the submit never reloads', async () => {
    const rig = setup();

    const event = submitThroughDom(rig, 'known@nabd.test');
    expect(event.defaultPrevented).toBe(true); // a native submit would reload the app

    const request = rig.http.expectOne(URL);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ email: 'known@nabd.test' });

    noContent(request);
    await rig.fixture.whenStable();
    rig.fixture.detectChanges();

    expect(rig.host.querySelector('form')).toBeNull();
    expect(text(rig)).toContain(NEUTRAL);
  });

  it('answers a known address and an unknown one with the same sentence', async () => {
    const known = setup();
    submitThroughDom(known, 'known@nabd.test');
    const knownRequest = known.http.expectOne(URL);
    noContent(knownRequest);
    await known.fixture.whenStable();
    known.fixture.detectChanges();

    const unknown = setup();
    submitThroughDom(unknown, 'nobody@nabd.test');
    const unknownRequest = unknown.http.expectOne(URL);
    noContent(unknownRequest);
    await unknown.fixture.whenStable();
    unknown.fixture.detectChanges();

    // the screen cannot tell the two apart, because the door does not (A3)
    expect(text(known)).toBe(text(unknown));
    expect(text(unknown)).toContain(NEUTRAL);

    // the bodies differ only in the address — nothing else travels
    expect(knownRequest.request.body).toEqual({ email: 'known@nabd.test' });
    expect(unknownRequest.request.body).toEqual({ email: 'nobody@nabd.test' });
  });

  it('shows the server’s own wait on a 429 and lets the visitor retry in place', async () => {
    const rig = setup();

    submitThroughDom(rig, 'waited@nabd.test');
    rig.http
      .expectOne(URL)
      .flush(
        { code: 'identity:login_backoff', delaySeconds: 42 },
        { status: 429, statusText: 'Too Many Requests' },
      );

    await rig.fixture.whenStable();
    rig.fixture.detectChanges();

    expect(text(rig)).toContain('42'); // never a guessed 30
    expect(rig.host.querySelector('form')).not.toBeNull(); // throttled, not locked out
    const input = rig.host.querySelector('input[type="email"]') as HTMLInputElement;
    expect(input.value).toBe('waited@nabd.test'); // the typed address survives

    // after the wait, the same screen sends again
    const retry = new Event('submit', { cancelable: true, bubbles: true });
    rig.host.querySelector('form')!.dispatchEvent(retry);
    noContent(rig.http.expectOne(URL));
    await rig.fixture.whenStable();
    rig.fixture.detectChanges();

    expect(text(rig)).toContain(NEUTRAL);
  });

  it('keeps the typed address when the server cannot be reached', async () => {
    const rig = setup();

    submitThroughDom(rig, 'offline@nabd.test');
    rig.http.expectOne(URL).flush(null, { status: 0, statusText: 'Unknown Error' });

    await rig.fixture.whenStable();
    rig.fixture.detectChanges();

    const en = DICTIONARIES['en'] as unknown as { errors: { network: string } };
    expect(text(rig)).toContain(en.errors.network);

    const input = rig.host.querySelector('input[type="email"]') as HTMLInputElement;
    expect(input.value).toBe('offline@nabd.test');
  });

  it('renders the neutral sentence from the Arabic dictionary', async () => {
    const rig = setup();

    TestBed.inject(TranslocoService).setActiveLang('ar');
    await rig.fixture.whenStable();
    rig.fixture.detectChanges();

    submitThroughDom(rig, 'ar@nabd.test');
    noContent(rig.http.expectOne(URL));
    await rig.fixture.whenStable();
    rig.fixture.detectChanges();

    const ar = DICTIONARIES['ar'] as unknown as { auth: { forgotSent: string; forgotBack: string } };
    expect(text(rig)).toContain(ar.auth.forgotSent);
    expect(text(rig)).toContain(ar.auth.forgotBack);
    expect(text(rig)).not.toContain(NEUTRAL);
  });

  it('the shared card’s toggle flips the document without leaving the page', () => {
    const rig = setup();

    (rig.host.querySelector('.locale-toggle') as HTMLButtonElement).click();

    expect(document.documentElement.getAttribute('dir')).toBe('rtl');
    expect(document.documentElement.getAttribute('lang')).toBe('ar');
  });

  it('is an anonymous top-level door, not a child of the authenticated shell', () => {
    const route = routes.find((candidate) => candidate.path === 'forgot-password');

    expect(route?.component).toBe(ForgotPasswordPage);
    expect(route?.canActivate).toBeUndefined();

    const shell = routes.find((candidate) => candidate.component === Shell);
    expect(shell?.children?.some((child) => child.path === 'forgot-password')).toBe(false);
  });
});
