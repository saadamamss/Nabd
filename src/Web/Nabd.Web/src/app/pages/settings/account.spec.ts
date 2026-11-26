import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  TestRequest,
} from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { TranslocoService } from '@jsverse/transloco';
import { provideTestTransloco } from '../../testing/test-transloco';
import { DICTIONARIES } from '../../testing/test-dictionaries';
import { authGuard } from '../../core/auth/auth.guard';
import { AuthService } from '../../core/auth/auth.service';
import { NoticeService } from '../../core/ui/notice.service';
import { routes } from '../../app.routes';
import { Shell } from '../../layout/shell/shell';
import { AccountPage } from './account';

const URL = '/api/v1/auth/t1/users/u1/password';

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
  fixture: ComponentFixture<AccountPage>;
  http: HttpTestingController;
  host: HTMLElement;
}

/** The rig a fact built; null when a fact needs no HTTP (the route fact is static). */
let controller: HttpTestingController | null = null;

function setup(): Rig {
  controller?.verify();
  controller = null;
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    imports: [AccountPage],
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      ...provideTestTransloco(),
    ],
  });

  const http = TestBed.inject(HttpTestingController);
  controller = http;
  TestBed.inject(AuthService).apply(SESSION);

  const fixture = TestBed.createComponent(AccountPage);
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
function submitThroughDom(
  rig: Rig,
  current: string,
  password: string,
  confirm = password,
): Event {
  rig.fixture.componentInstance.form.setValue({ current, password, confirm });
  rig.fixture.detectChanges();

  const form = rig.host.querySelector('form')!;
  const event = new Event('submit', { cancelable: true, bubbles: true });
  form.dispatchEvent(event);
  return event;
}

describe('AccountPage', () => {
  beforeEach(() => {
    sessionStorage.clear();
    localStorage.clear();
  });

  afterEach(() => controller?.verify());

  it('changes the password, signs out honestly, and never calls /logout', async () => {
    const rig = setup();
    const auth = TestBed.inject(AuthService);
    const notice = TestBed.inject(NoticeService);
    const navigate = vi
      .spyOn(TestBed.inject(Router), 'navigate')
      .mockResolvedValue(true);

    const event = submitThroughDom(rig, 'CorrectHorse1', 'GoodPass12');
    expect(event.defaultPrevented).toBe(true);

    const request = rig.http.expectOne(URL);
    expect(request.request.method).toBe('PATCH');
    expect(request.request.body).toEqual({
      currentPassword: 'CorrectHorse1',
      newPassword: 'GoodPass12',
    });

    noContent(request);
    await rig.fixture.whenStable();
    rig.fixture.detectChanges();

    // the door revoked every refresh token: the local session must go too
    expect(auth.user()).toBeNull();
    expect(navigate).toHaveBeenCalledWith(['/login']);
    expect(notice.notices().map((item) => item.key)).toContain('auth.passwordChanged');

    // ...and no /logout: the cookie is already dead (A2)
    rig.http.expectNone('/api/v1/auth/logout');
  });

  it('anchors a wrong current password at its field and keeps the session', async () => {
    const rig = setup();
    const auth = TestBed.inject(AuthService);
    const navigate = vi
      .spyOn(TestBed.inject(Router), 'navigate')
      .mockResolvedValue(true);

    submitThroughDom(rig, 'WrongHorse1', 'GoodPass12');
    rig.http
      .expectOne(URL)
      .flush(
        { code: 'identity:invalid_credentials' },
        { status: 401, statusText: 'Unauthorized' },
      );

    await rig.fixture.whenStable();
    rig.fixture.detectChanges();

    expect(text(rig)).toContain('That is not your current password.');
    expect(auth.isAuthenticated()).toBe(true); // the C1 defect, pinned at the page
    expect(navigate).not.toHaveBeenCalled();
    expect(rig.host.querySelector('form')).not.toBeNull();
  });

  it('anchors a weak new password at its field with the map’s wording', async () => {
    const rig = setup();

    submitThroughDom(rig, 'CorrectHorse1', 'short77');
    rig.http
      .expectOne(URL)
      .flush(
        { code: 'identity:weak_password' },
        { status: 400, statusText: 'Bad Request' },
      );

    await rig.fixture.whenStable();
    rig.fixture.detectChanges();

    expect(text(rig)).toContain('The password must be at least 8 characters.');
    expect(rig.host.querySelector('form')).not.toBeNull();
  });

  it('a confirm mismatch blocks the submit: nothing is sent', () => {
    const rig = setup();

    submitThroughDom(rig, 'CorrectHorse1', 'GoodPass12', 'GoodPass13');
    rig.fixture.detectChanges();

    expect(text(rig)).toContain('The two passwords do not match.');
    // afterEach's verify() is the second proof: no request was made
    rig.http.expectNone(URL);
  });

  it('renders in Arabic from the dictionary (RTL comes from the document)', async () => {
    const rig = setup();

    TestBed.inject(TranslocoService).setActiveLang('ar');
    await rig.fixture.whenStable();
    rig.fixture.detectChanges();

    const ar = DICTIONARIES['ar'] as unknown as {
      settings: { accountTitle: string; changePassword: string; wrongCurrent: string };
    };

    expect(text(rig)).toContain(ar.settings.accountTitle);
    expect(text(rig)).toContain(ar.settings.changePassword);
    expect(rig.host.querySelectorAll('input[type="password"]').length).toBe(3);
  });

  it('is guarded by the shell, and /settings lands on the account page (C5)', () => {
    const shell = routes.find((candidate) => candidate.component === Shell);
    expect(shell?.canActivate).toContain(authGuard);

    const children = shell?.children ?? [];
    const account = children.find((child) => child.path === 'settings/account');
    expect(account?.component).toBe(AccountPage);

    const settings = children.find((child) => child.path === 'settings');
    expect(settings?.redirectTo).toBe('settings/account');
    expect(settings?.pathMatch).toBe('full');

    // the stub's copy is gone: /settings is no longer a placeholder
    const en = DICTIONARIES['en'] as unknown as { pages: Record<string, unknown> };
    const ar = DICTIONARIES['ar'] as unknown as { pages: Record<string, unknown> };
    expect(en.pages['settings']).toBeUndefined();
    expect(ar.pages['settings']).toBeUndefined();
  });
});
