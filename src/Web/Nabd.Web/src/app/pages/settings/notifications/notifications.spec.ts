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
import { routes } from '../../../app.routes';
import { Shell } from '../../../layout/shell/shell';
import { AccountPage } from '../account';
import { NotificationsPage } from './notifications';

const URL = '/api/v1/me/telegram';
const VERIFIED = '2026-11-20T09:30:00Z';
const TICKET = {
  code: 'K7M2Q9X4P1R8T3V6',
  expiresAt: '2026-11-27T12:10:00Z',
  botUsername: 'nabd_bot',
};

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

function setup(role = 'tenant_admin'): Rig {
  controller?.verify();
  controller = null;
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

  return { fixture, http, host: fixture.nativeElement as HTMLElement };
}

function text(rig: Rig): string {
  return rig.host.textContent ?? '';
}

function state(rig: Rig, body: object): void {
  rig.http.expectOne(URL).flush(body);
  rig.fixture.detectChanges();
}

function openDialog(rig: Rig, ticket: object = TICKET): void {
  click(rig, 'link');
  const post = rig.http.expectOne({ method: 'POST', url: `${URL}/link` });
  post.flush(ticket);
  rig.fixture.detectChanges();
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
    controller?.verify();
    controller = null;
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
    // severities and unlink are S3 — this page half promises nothing it lacks
    expect(rig.host.querySelectorAll('input').length).toBe(0);
  });

  it('the ticket is shown once, with the deep link, and leaves the DOM on close', () => {
    const rig = setup();
    state(rig, { linked: false, severities: ['warn', 'critical'] });

    openDialog(rig);

    expect(dialog(rig)).not.toBeNull();

    const blocks = rig.host.querySelectorAll('[data-link-code]');
    expect(blocks.length).toBe(1);
    expect(blocks[0].textContent?.trim()).toBe(TICKET.code);
    expect(blocks[0].getAttribute('dir')).toBe('ltr');

    const link = rig.host.querySelector('[data-bot-link]') as HTMLAnchorElement | null;
    expect(link?.getAttribute('href')).toBe(
      `https://t.me/${TICKET.botUsername}?start=${TICKET.code}`,
    );

    click(rig, 'close-link');

    expect(dialog(rig)).toBeNull();
    expect(text(rig)).not.toContain(TICKET.code);
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
      expect(window.location.href).not.toContain(TICKET.code);
      for (const spy of logs) {
        for (const call of spy.mock.calls) {
          expect(String(call[0] ?? '')).not.toContain(TICKET.code);
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

    openDialog(rig, { code: TICKET.code, expiresAt: TICKET.expiresAt });

    expect(rig.host.querySelector('[data-bot-link]')).toBeNull();
    expect(rig.host.querySelector('a[href^="https://t.me"]')).toBeNull();
    expect(text(rig)).toContain(TICKET.code);
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
    expect(codeBlock(rig)?.textContent?.trim()).toBe(TICKET.code);
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
});
