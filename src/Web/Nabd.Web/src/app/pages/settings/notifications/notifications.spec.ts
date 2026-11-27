import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { TranslocoService } from '@jsverse/transloco';
import { provideTestTransloco } from '../../../testing/test-transloco';
import { DICTIONARIES } from '../../../testing/test-dictionaries';
import { authGuard } from '../../../core/auth/auth.guard';
import { AuthService } from '../../../core/auth/auth.service';
import { FeedbackUi } from '../../../core/ui/feedback-ui';
import { NoticeService } from '../../../core/ui/notice.service';
import { routes } from '../../../app.routes';
import { Shell } from '../../../layout/shell/shell';
import { AccountPage } from '../account';
import { NotificationsPage } from './notifications';

const URL = '/api/v1/me/telegram';
const VERIFIED = '2026-11-20T09:30:00Z';
const CODE = 'K7M2Q9X4P1R8T3V6';
const BOT = 'nabd_bot';

/** A ticket whose window starts *now* — the server issues ten minutes from its clock. */
function ticketFor(
  ms: number,
  overrides: { code?: string; botUsername?: string | null } = {},
): { code: string; expiresAt: string; botUsername?: string } {
  const ticket: { code: string; expiresAt: string; botUsername?: string } = {
    code: overrides.code ?? CODE,
    expiresAt: new Date(Date.now() + ms).toISOString(),
  };
  if (overrides.botUsername !== null) ticket.botUsername = overrides.botUsername ?? BOT;
  return ticket;
}

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
  fixture: ComponentFixture<NotificationsPage>;
  http: HttpTestingController;
  host: HTMLElement;
}

/** The rig a fact built; null when a fact needs no HTTP (the route fact is static). */
let controller: HttpTestingController | null = null;
/** The live rig, so the page's timers are stopped before the module resets. */
let current: Rig | null = null;

function setup(role = 'tenant_admin'): Rig {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    imports: [NotificationsPage],
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      ...provideTestTransloco(),
    ],
  });

  const http = TestBed.inject(HttpTestingController);
  controller = http;
  window.localStorage.clear();
  window.sessionStorage.clear();
  TestBed.inject(AuthService).apply({
    ...SESSION,
    user: { ...SESSION.user, role },
  });

  const fixture = TestBed.createComponent(NotificationsPage);
  fixture.detectChanges();

  const rig = { fixture, http, host: fixture.nativeElement as HTMLElement };
  current = rig;
  return rig;
}

function text(rig: Rig): string {
  return rig.host.textContent ?? '';
}

function state(rig: Rig, body: object): void {
  rig.http.expectOne(URL).flush(body);
  rig.fixture.detectChanges();
}

function openDialog(
  rig: Rig,
  ticket: object = ticketFor(10 * 60_000),
): void {
  click(rig, 'link');
  const post = rig.http.expectOne({ method: 'POST', url: `${URL}/link` });
  post.flush(ticket);
  rig.fixture.detectChanges();
}

/**
 * Moves the fake clock one second at a time and answers every poll on the way:
 * the page allows one poll in flight, so the next beat must not meet an
 * unanswered previous one.
 */
function advance(rig: Rig, ms: number): void {
  for (let elapsed = 0; elapsed < ms; elapsed += 1000) {
    vi.advanceTimersByTime(Math.min(1000, ms - elapsed));
    for (const poll of rig.http.match(URL)) {
      poll.flush({ linked: false, severities: ['warn', 'critical'] });
    }
    rig.fixture.detectChanges();
  }
}

function click(rig: Rig, action: string): void {
  const button = rig.host.querySelector(`[data-action="${action}"]`) as HTMLElement | null;
  expect(button, `the ${action} control must exist`).not.toBeNull();
  button!.click();
  rig.fixture.detectChanges();
}

function dialog(rig: Rig): HTMLElement | null {
  return rig.host.querySelector('[role="dialog"]');
}

function codeBlock(rig: Rig): HTMLElement | null {
  return rig.host.querySelector('[data-link-code]');
}

describe('NotificationsPage', () => {
  afterEach(() => {
    // the page owns two intervals while the dialog is open; stop them with the fixture
    current?.fixture.destroy();
    current = null;
    controller?.verify();
    controller = null;
    vi.useRealTimers();
  });

  it('unlinked: offers the link button, and no severity control is drawn yet', () => {
    const rig = setup();
    state(rig, { linked: false, severities: ['warn', 'critical'] });

    const en = DICTIONARIES['en'] as unknown as {
      settings: { telegramUnlinked: string; linkTelegram: string };
    };

    expect(text(rig)).toContain(en.settings.telegramUnlinked);
    expect(rig.host.querySelector('[data-action="link"]')?.textContent).toContain(
      en.settings.linkTelegram,
    );
    expect(rig.host.querySelectorAll('input').length).toBe(0);
    expect(rig.host.querySelector('[data-card="telegram"]')?.textContent).not.toContain(
      'Linked since',
    );
  });

  it('linked: shows the verified date in the active locale and hides the link button', () => {
    const rig = setup();
    state(rig, { linked: true, verifiedAt: VERIFIED, severities: ['warn', 'critical'] });

    const when = new Intl.DateTimeFormat('en', {
      dateStyle: 'medium',
      timeStyle: 'short',
    }).format(new Date(VERIFIED));

    expect(text(rig)).toContain(when);
    expect(rig.host.querySelector('[data-action="link"]')).toBeNull();
    // S3 arrived: a linked state now carries the boxes and the unlink door
    expect(rig.host.querySelector('[data-severity="warn"]')).not.toBeNull();
    expect(rig.host.querySelector('[data-severity="critical"]')).not.toBeNull();
    expect(rig.host.querySelector('[data-action="unlink"]')).not.toBeNull();
  });

  it('the ticket is shown once, with the deep link, and leaves the DOM on close', () => {
    const rig = setup();
    state(rig, { linked: false, severities: ['warn', 'critical'] });

    openDialog(rig);

    expect(dialog(rig)).not.toBeNull();

    const blocks = rig.host.querySelectorAll('[data-link-code]');
    expect(blocks.length).toBe(1);
    expect(blocks[0].textContent?.trim()).toBe(CODE);
    expect(blocks[0].getAttribute('dir')).toBe('ltr');

    const link = rig.host.querySelector('[data-bot-link]') as HTMLAnchorElement | null;
    expect(link?.getAttribute('href')).toBe(
      `https://t.me/${BOT}?start=${CODE}`,
    );

    click(rig, 'close-link');

    expect(dialog(rig)).toBeNull();
    expect(text(rig)).not.toContain(CODE);
  });

  it('the dialog watches the link every 3 s, then closes itself when the server says linked', () => {
    vi.useFakeTimers();
    const rig = setup();
    state(rig, { linked: false, severities: ['warn', 'critical'] });
    openDialog(rig);

    // nothing before the beat: no hidden poll on open
    vi.advanceTimersByTime(2999);
    expect(rig.http.match(URL).length).toBe(0);

    vi.advanceTimersByTime(1);
    const poll = rig.http.expectOne(URL);
    expect(poll.request.method).toBe('GET');
    poll.flush({ linked: true, verifiedAt: VERIFIED, severities: ['warn', 'critical'] });
    rig.fixture.detectChanges();

    const when = new Intl.DateTimeFormat('en', {
      dateStyle: 'medium',
      timeStyle: 'short',
    }).format(new Date(VERIFIED));

    expect(dialog(rig)).toBeNull();
    expect(text(rig)).not.toContain(CODE);
    expect(text(rig)).toContain(when);

    const notices = TestBed.inject(NoticeService);
    expect(notices.notices()[0]?.key).toBe('settings.linkSuccess');
    expect(notices.notices()[0]?.kind).toBe('success');
    for (const notice of notices.notices()) notices.dismiss(notice.id);

    // the observation is over: no beat after success
    vi.advanceTimersByTime(30_000);
    expect(rig.http.match(URL).length).toBe(0);
  });

  it('closing or leaving the page stops the watch (N10)', () => {
    vi.useFakeTimers();
    const rig = setup();
    state(rig, { linked: false, severities: ['warn', 'critical'] });
    openDialog(rig);

    click(rig, 'close-link');
    vi.advanceTimersByTime(30_000);
    expect(rig.http.match(URL).length).toBe(0);

    openDialog(rig);
    vi.advanceTimersByTime(3000);
    rig.http.expectOne(URL).flush({ linked: false, severities: ['warn', 'critical'] });

    rig.fixture.destroy();
    current = null;
    vi.advanceTimersByTime(30_000);
    expect(rig.http.match(URL).length).toBe(0);
  });

  it('expiry: the countdown runs to zero, the code leaves the page, and a new code can be asked for', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-11-27T12:00:00Z'));
    const rig = setup();
    state(rig, { linked: false, severities: ['warn', 'critical'] });

    openDialog(rig, ticketFor(6_000));

    const countdown = () => rig.host.querySelector('[data-countdown]')?.textContent?.trim();
    expect(countdown()).toBe('00:06');
    expect(rig.host.querySelector('[data-countdown]')?.getAttribute('dir')).toBe('ltr');

    advance(rig, 1000);
    expect(countdown()).toBe('00:05');

    advance(rig, 5000);

    // zero: the dialog stays, the code is gone, the clock stopped, nothing claimed
    const en = DICTIONARIES['en'] as unknown as {
      settings: { linkExpired: string; newCode: string };
    };
    expect(dialog(rig)).not.toBeNull();
    expect(rig.host.querySelector('[data-link-code]')).toBeNull();
    expect(text(rig)).not.toContain(CODE);
    expect(text(rig)).toContain(en.settings.linkExpired);
    expect(rig.host.querySelector('[data-action="new-code"]')?.textContent).toContain(
      en.settings.newCode,
    );
    expect(TestBed.inject(NoticeService).notices().length).toBe(0);

    // raw clock: an expired window must leave no beat behind
    vi.advanceTimersByTime(30_000);
    expect(rig.http.match(URL).length).toBe(0);

    // a fresh ticket reopens the same dialog with a full window
    click(rig, 'new-code');
    rig.http.expectOne({ method: 'POST', url: `${URL}/link` }).flush(ticketFor(10 * 60_000));
    rig.fixture.detectChanges();

    expect(rig.host.querySelector('[data-link-code]')?.textContent?.trim()).toBe(CODE);
    expect(countdown()).toBe('10:00');
  });

  it('the code never reaches storage, the URL, or a log line (C6/N7)', () => {
    const rig = setup();
    state(rig, { linked: false, severities: ['warn', 'critical'] });

    const logs = [
      vi.spyOn(console, 'log'),
      vi.spyOn(console, 'warn'),
      vi.spyOn(console, 'error'),
      vi.spyOn(console, 'info'),
      vi.spyOn(console, 'debug'),
    ];

    try {
      openDialog(rig);

      expect(window.localStorage.length).toBe(0);
      expect(window.sessionStorage.length).toBe(0);
      expect(window.location.href).not.toContain(CODE);
      for (const spy of logs) {
        for (const call of spy.mock.calls) {
          expect(String(call[0] ?? '')).not.toContain(CODE);
        }
      }
    } finally {
      for (const spy of logs) spy.mockRestore();
    }
  });

  it('copy never claims success when the clipboard is absent (C6)', () => {
    const rig = setup();
    state(rig, { linked: false, severities: ['warn', 'critical'] });
    openDialog(rig);

    const original = Object.getOwnPropertyDescriptor(navigator, 'clipboard');
    Object.defineProperty(navigator, 'clipboard', { value: undefined, configurable: true });

    try {
      click(rig, 'copy');
      const en = DICTIONARIES['en'] as unknown as { common: { copied: string } };
      expect(text(rig)).not.toContain(en.common.copied);
    } finally {
      if (original) Object.defineProperty(navigator, 'clipboard', original);
      else Reflect.deleteProperty(navigator, 'clipboard');
    }
  });

  it('a failed read says so honestly, and retry asks again', () => {
    const rig = setup();
    rig.http.expectOne(URL).flush(null, { status: 500, statusText: 'Server Error' });
    rig.fixture.detectChanges();

    const en = DICTIONARIES['en'] as unknown as { errors: { unexpected: string } };
    expect(text(rig)).toContain(en.errors.unexpected);
    expect(rig.host.querySelector('[data-action="link"]')).toBeNull();

    click(rig, 'retry');
    rig.http.expectOne(URL).flush({ linked: false, severities: ['warn', 'critical'] });
    rig.fixture.detectChanges();

    expect(rig.host.querySelector('[data-action="link"]')).not.toBeNull();
  });

  it('without a bot username the dialog drops the t.me link and carries the code instead', () => {
    const rig = setup();
    state(rig, { linked: false, severities: ['warn', 'critical'] });

    openDialog(rig, ticketFor(10 * 60_000, { botUsername: null }));

    expect(rig.host.querySelector('[data-bot-link]')).toBeNull();
    expect(rig.host.querySelector('a[href^="https://t.me"]')).toBeNull();
    expect(text(rig)).toContain(CODE);
  });

  it('the email card states the platform truth and renders no control', () => {
    const rig = setup();
    state(rig, { linked: false, severities: ['warn', 'critical'] });

    const en = DICTIONARIES['en'] as unknown as {
      settings: { emailBody: string; emailFeedback: string };
    };
    const card = rig.host.querySelector('[data-card="email"]') as HTMLElement | null;

    expect(card).not.toBeNull();
    expect(card!.textContent).toContain(en.settings.emailBody);
    expect(card!.querySelectorAll('input, select, textarea').length).toBe(0);
    expect(card!.querySelectorAll('[role="switch"]').length).toBe(0);

    const feedback = TestBed.inject(FeedbackUi);
    expect(feedback.requests()).toBe(0);
    click(rig, 'feedback');
    expect(feedback.requests()).toBe(1);
    expect(card!.textContent).toContain(en.settings.emailFeedback);
  });

  it('every role reaches the page — nothing in the UI hides it', () => {
    for (const role of ['viewer', 'tenant_admin', 'platform_admin']) {
      const rig = setup(role);
      state(rig, { linked: false, severities: ['warn', 'critical'] });
      expect(rig.host.querySelector('[data-action="link"]'), `${role} must see the page`).not.toBeNull();
    }
  });

  it('renders in Arabic, and the code stays numeric-LTR under dir=rtl', async () => {
    const rig = setup();
    state(rig, { linked: false, severities: ['warn', 'critical'] });

    TestBed.inject(TranslocoService).setActiveLang('ar');
    await rig.fixture.whenStable();

    openDialog(rig);

    const ar = DICTIONARIES['ar'] as unknown as {
      settings: { notificationsTitle: string; linkTitle: string };
    };

    expect(text(rig)).toContain(ar.settings.notificationsTitle);
    expect(dialog(rig)?.textContent).toContain(ar.settings.linkTitle);
    expect(codeBlock(rig)?.getAttribute('dir')).toBe('ltr');
    expect(codeBlock(rig)?.textContent?.trim()).toBe(CODE);
    expect(rig.host.querySelector('[data-countdown]')?.getAttribute('dir')).toBe('ltr');
  });

  it('the tabs name both settings pages and mark the current one (C1)', async () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      imports: [NotificationsPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([
          { path: 'settings/account', component: AccountPage },
          { path: 'settings/notifications', component: NotificationsPage },
        ]),
        ...provideTestTransloco(),
      ],
    });

    const http = TestBed.inject(HttpTestingController);
    controller = http;
    const router = TestBed.inject(Router);
    await router.navigateByUrl('/settings/notifications');

    const fixture = TestBed.createComponent(NotificationsPage);
    fixture.detectChanges();
    http.expectOne(URL).flush({ linked: false, severities: ['warn', 'critical'] });
    await fixture.whenStable();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const account = host.querySelector('[data-tab="account"]') as HTMLAnchorElement | null;
    const notifications = host.querySelector(
      '[data-tab="notifications"]',
    ) as HTMLAnchorElement | null;

    expect(account?.getAttribute('href')).toBe('/settings/account');
    expect(notifications?.getAttribute('href')).toBe('/settings/notifications');
    expect(notifications?.classList.contains('is-active')).toBe(true);
    expect(notifications?.getAttribute('aria-current')).toBe('page');
    expect(account?.classList.contains('is-active')).toBe(false);
  });

  it('the route sits in the guarded shell, beside the account page (C1)', () => {
    const shell = routes.find((candidate) => candidate.component === Shell);
    expect(shell?.canActivate).toContain(authGuard);

    const children = shell?.children ?? [];
    expect(children.find((child) => child.path === 'settings/notifications')?.component).toBe(
      NotificationsPage,
    );
    expect(children.find((child) => child.path === 'settings/account')?.component).toBe(
      AccountPage,
    );
    expect(children.find((child) => child.path === 'settings')?.redirectTo).toBe(
      'settings/account',
    );
  });

  // ---- S3: the controls -------------------------------------------------

  function linked(rig: Rig): void {
    state(rig, { linked: true, verifiedAt: VERIFIED, severities: ['warn', 'critical'] });
  }

  function severity(rig: Rig, value: string): HTMLInputElement {
    const box = rig.host.querySelector(`[data-severity="${value}"]`) as HTMLInputElement | null;
    expect(box, `the ${value} checkbox must exist`).not.toBeNull();
    return box!;
  }

  function patch(rig: Rig) {
    return rig.http.expectOne({ method: 'PATCH', url: `${URL}/severities` });
  }

  it('linked: the date, both server-chosen boxes and unlink — and info is never enabled (N4)', () => {
    const rig = setup();
    state(rig, { linked: true, verifiedAt: VERIFIED, severities: ['critical'] });

    const en = DICTIONARIES['en'] as unknown as {
      settings: { severitiesTitle: string; severityInfoHint: string };
    };

    expect(text(rig)).toContain(en.settings.severitiesTitle);
    // the boxes render the SERVER's list, not a hard-coded default (C10)
    expect(severity(rig, 'warn').checked).toBe(false);
    expect(severity(rig, 'critical').checked).toBe(true);
    expect(severity(rig, 'warn').disabled).toBe(false);
    expect(severity(rig, 'critical').disabled).toBe(false);

    expect(severity(rig, 'info').disabled).toBe(true);
    expect(severity(rig, 'info').checked).toBe(false);
    expect(text(rig)).toContain(en.settings.severityInfoHint);

    expect(rig.host.querySelector('[data-action="unlink"]')).not.toBeNull();
    expect(rig.host.querySelectorAll('input[type="checkbox"]:not([disabled])').length).toBe(2);
  });

  it('a toggle sends the whole list, disables both boxes in flight, then renders the answer (N9)', () => {
    const rig = setup();
    linked(rig);

    severity(rig, 'warn').click();
    rig.fixture.detectChanges();
    const save = patch(rig);

    expect(save.request.body).toEqual({ severities: ['critical'] });
    // one write at a time: neither box may start a second request
    expect(severity(rig, 'warn').disabled).toBe(true);
    expect(severity(rig, 'critical').disabled).toBe(true);

    save.flush({ linked: true, verifiedAt: VERIFIED, severities: ['critical'] });
    rig.fixture.detectChanges();

    expect(severity(rig, 'warn').checked).toBe(false);
    expect(severity(rig, 'critical').checked).toBe(true);
    expect(severity(rig, 'warn').disabled).toBe(false);
  });

  it('silence is a real choice, and a 422 puts the box back on the server truth (fact 6)', () => {
    const rig = setup();
    linked(rig);

    const en = DICTIONARIES['en'] as unknown as {
      settings: { silenceWarning: string };
      errors: { invalidSeverity: string };
    };

    // warn off, then critical off → an explicit [], which is silence, not a fault
    severity(rig, 'warn').click();
    patch(rig).flush({ linked: true, verifiedAt: VERIFIED, severities: ['critical'] });
    rig.fixture.detectChanges();

    severity(rig, 'critical').click();
    const empty = patch(rig);
    expect(empty.request.body).toEqual({ severities: [] });
    empty.flush({ linked: true, verifiedAt: VERIFIED, severities: [] });
    rig.fixture.detectChanges();

    expect(rig.host.querySelector('[data-silence-warning]')?.textContent).toContain(
      en.settings.silenceWarning,
    );
    expect(severity(rig, 'critical').checked).toBe(false);

    // a refusal: the server never took the pick, so the box must not keep it
    severity(rig, 'warn').click();
    patch(rig).flush(
      { code: 'notifications:invalid_severity' },
      { status: 422, statusText: 'Unprocessable Entity' },
    );
    rig.fixture.detectChanges();

    expect(severity(rig, 'warn').checked).toBe(false);
    expect(text(rig)).toContain(en.errors.invalidSeverity);
    expect(rig.host.querySelector('[data-silence-warning]')).not.toBeNull();
  });

  it('unlink asks first, then DELETE flips the card and the boxes leave (N6/fact 7)', () => {
    const rig = setup();
    linked(rig);

    const en = DICTIONARIES['en'] as unknown as {
      settings: { unlinkBody: string; telegramUnlinked: string; linkTelegram: string };
    };
    const when = new Intl.DateTimeFormat('en', {
      dateStyle: 'medium',
      timeStyle: 'short',
    }).format(new Date(VERIFIED));

    click(rig, 'unlink');
    expect(rig.host.querySelector('[data-unlink-dialog]')).not.toBeNull();
    expect(text(rig)).toContain(en.settings.unlinkBody);

    // cancel is not a hidden DELETE
    click(rig, 'cancel-unlink');
    expect(rig.host.querySelector('[data-unlink-dialog]')).toBeNull();
    expect(rig.http.match({ url: `${URL}/link` }).length).toBe(0);
    expect(severity(rig, 'warn').checked).toBe(true);

    click(rig, 'unlink');
    click(rig, 'confirm-unlink');
    const remove = rig.http.expectOne({ method: 'DELETE', url: `${URL}/link` });
    remove.flush({ linked: false, severities: ['warn', 'critical'] });
    rig.fixture.detectChanges();

    expect(rig.host.querySelector('[data-unlink-dialog]')).toBeNull();
    expect(rig.host.querySelector('[data-action="link"]')?.textContent).toContain(
      en.settings.linkTelegram,
    );
    expect(rig.host.querySelectorAll('input').length).toBe(0);
    expect(text(rig)).toContain(en.settings.telegramUnlinked);
    expect(text(rig)).not.toContain(when);
  });

  it('a severities 200 is not a link: a PATCH answered unlinked stays unlinked (C3/fact 7b)', () => {
    const rig = setup();
    linked(rig);

    severity(rig, 'critical').click();
    patch(rig).flush({ linked: false, severities: ['warn'] });
    rig.fixture.detectChanges();

    expect(rig.host.querySelector('[data-action="link"]')).not.toBeNull();
    expect(rig.host.querySelector('[data-action="unlink"]')).toBeNull();
    expect(rig.host.querySelectorAll('input').length).toBe(0);
  });
});
