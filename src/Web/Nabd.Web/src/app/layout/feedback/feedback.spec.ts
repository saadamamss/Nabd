import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { provideTestTransloco } from '../../testing/test-transloco';
import { AuthService } from '../../core/auth/auth.service';
import { NoticeService } from '../../core/ui/notice.service';
import { routes } from '../../app.routes';
import { Shell } from '../shell/shell';
import { FeedbackButton } from './feedback';

const SESSION = {
  user: {
    userId: 'u1',
    tenantId: 't1',
    email: 'admin@nabd.test',
    role: 'tenant_admin',
    locale: 'en',
    displayName: 'Ada Admin',
  },
  accessToken: 'access-1',
};

function setup(url = '/alerts?tab=1'): {
  fixture: ComponentFixture<FeedbackButton>;
  http: HttpTestingController;
  component: FeedbackButton;
} {
  TestBed.configureTestingModule({
    imports: [FeedbackButton],
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      ...provideTestTransloco(),
      // the component only reads the pathname; the query must not travel
      { provide: Router, useValue: { url } },
    ],
  });

  const http = TestBed.inject(HttpTestingController);
  const fixture = TestBed.createComponent(FeedbackButton);
  return { fixture, http, component: fixture.componentInstance };
}

function host(fixture: ComponentFixture<FeedbackButton>): HTMLElement {
  return fixture.nativeElement as HTMLElement;
}

function text(fixture: ComponentFixture<FeedbackButton>): string {
  return host(fixture).textContent ?? '';
}

describe('FeedbackButton', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('sends the trimmed message with the pathname, the locale and the reply address', async () => {
    const { fixture, http, component } = setup();
    fixture.detectChanges();
    component.open();
    fixture.detectChanges();

    component.message.set('  التبريد صامت  ');
    component.contact.set(' me@nabd.test ');
    fixture.detectChanges();

    component.send();

    const request = http.expectOne('/api/v1/feedback');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      message: 'التبريد صامت',
      page: '/alerts',              // the query string never travels
      locale: 'en',
      contactEmail: 'me@nabd.test',
    });

    // 201: the door's deliberate answer — the UI accepts it (no 200 assumption)
    request.flush(
      { id: 'f1', createdAt: '2026-11-26T00:00:00Z' },
      { status: 201, statusText: 'Created' },
    );

    await fixture.whenStable();
    fixture.detectChanges();

    expect(component.isOpen()).toBe(false);
    expect(component.message()).toBe('');
    expect(TestBed.inject(NoticeService).notices().map((notice) => notice.key)).toContain(
      'feedback.sent',
    );
  });

  it('the form submit is intercepted — the page must never navigate', () => {
    const { fixture, http, component } = setup();
    component.open();
    component.message.set('no reload please');
    fixture.detectChanges();

    const form = host(fixture).querySelector('form')!;
    const event = new Event('submit', { cancelable: true, bubbles: true });
    form.dispatchEvent(event);

    // a native, unprevented submit is a GET to the same URL: the app reloads
    // and the typed message is gone (the defect this fact exists for)
    expect(event.defaultPrevented).toBe(true);

    const request = http.expectOne('/api/v1/feedback');
    expect(request.request.method).toBe('POST');
    request.flush(
      { id: 'f3', createdAt: '2026-11-26T00:00:00Z' },
      { status: 201, statusText: 'Created' },
    );
  });

  it('a refusal keeps the dialog and the typed text, and names the server code', async () => {
    const { fixture, http, component } = setup();
    component.open();
    component.message.set('keep this text');
    fixture.detectChanges();

    component.send();
    http
      .expectOne('/api/v1/feedback')
      .flush({ code: 'feedback:invalid_message' }, { status: 422, statusText: 'Unprocessable Entity' });

    await fixture.whenStable();
    fixture.detectChanges();

    expect(component.isOpen()).toBe(true);
    expect(component.message()).toBe('keep this text');
    expect(text(fixture)).toContain('Write a message first');
  });

  it('a 429 says how many seconds the server asked for', async () => {
    const { fixture, http, component } = setup();
    component.open();
    component.message.set('again');
    fixture.detectChanges();

    component.send();
    http
      .expectOne('/api/v1/feedback')
      .flush(
        { code: 'feedback:rate_limited', retryAfterSeconds: 17 },
        { status: 429, statusText: 'Too Many Requests' },
      );

    await fixture.whenStable();
    fixture.detectChanges();

    expect(component.isOpen()).toBe(true);
    expect(text(fixture)).toContain('17');
  });

  it('escape closes the dialog and keeps the draft', async () => {
    const { fixture, component } = setup();
    component.open();
    component.message.set('draft');
    fixture.detectChanges();

    host(fixture)
      .querySelector('.ui-overlay')!
      .dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    fixture.detectChanges();

    expect(component.isOpen()).toBe(false);
    expect(component.message()).toBe('draft');
  });

  it('the counter counts characters, not UTF-16 units', async () => {
    const { fixture, component } = setup();
    component.open();
    component.message.set('😀😀😀');    // three code points, six UTF-16 units
    fixture.detectChanges();

    await fixture.whenStable();
    fixture.detectChanges();

    expect(text(fixture)).toContain('3 / 2000');
  });

  it('a script tag is sent verbatim and never becomes an element', async () => {
    const { fixture, http, component } = setup();
    const payload = '<script>alert(1)</script>';

    component.open();
    component.message.set(payload);
    fixture.detectChanges();

    expect(host(fixture).querySelector('script')).toBeNull();   // text, not markup

    component.send();
    const request = http.expectOne('/api/v1/feedback');
    expect((request.request.body as { message: string }).message).toBe(payload);

    request.flush(
      { id: 'f2', createdAt: '2026-11-26T00:00:00Z' },
      { status: 201, statusText: 'Created' },
    );
    await fixture.whenStable();
  });
});

describe('Feedback placement', () => {
  it('only the anonymous doors render outside the shell, so the button is on every authenticated route', () => {
    const shell = routes.find((route) => route.component === Shell);
    expect(shell?.children?.length ?? 0).toBeGreaterThan(0);

    const outside = routes
      .filter((route) => route.component !== Shell && route.path !== '**')
      .map((route) => route.path);

    expect(outside).toEqual(['login', 'forgot-password', 'reset-password/:token']);
  });

  it('the shell renders the floating control', async () => {
    TestBed.configureTestingModule({
      imports: [Shell],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        ...provideTestTransloco(),
      ],
    });

    const auth = TestBed.inject(AuthService);
    auth.apply(SESSION);
    sessionStorage.clear();

    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(Shell);
    fixture.detectChanges();
    await fixture.whenStable();

    http
      .expectOne('/api/v1/tenants/t1')
      .flush({ tenantId: 't1', name: 'Cold Chain Co', isActive: true });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(host(fixture as never).querySelector('.feedback-fab')).not.toBeNull();
  });

  it('the floating control mirrors: its own block uses logical inline properties', () => {
    const scss = readFileSync(
      join(process.cwd(), 'src', 'app', 'layout', 'feedback', 'feedback.scss'),
      'utf8',
    );

    const start = scss.indexOf('.feedback-fab {');
    const block = scss.slice(start, scss.indexOf('}', start));

    // the side is a design choice (moved to the inline end on 2026-11-26); what
    // must hold is that it is LOGICAL — RTL then mirrors it with no second sheet
    expect(block).toMatch(/inset-inline-(start|end)/);
    expect(block).not.toMatch(/(^|[^-])\bleft:/);
    expect(block).not.toMatch(/(^|[^-])\bright:/);
  });
});
