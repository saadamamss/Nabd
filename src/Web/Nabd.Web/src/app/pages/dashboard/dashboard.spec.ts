import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { signal, WritableSignal } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import { provideTestTransloco } from '../../testing/test-transloco';
import { AuthService } from '../../core/auth/auth.service';
import { AssetRow } from '../../core/api/tenant-admin.service';
import { AssetStatusRow } from '../../core/api/dashboard.service';
import {
  DashboardSocket,
  DashboardSocketHandlers,
  RealtimeState,
} from '../../core/realtime/dashboard.socket';
import { DashboardPage } from './dashboard';

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

function asset(assetId: string, name: string, location: string | null = 'Aisle 1'): AssetRow {
  return { assetId, tenantId: 't1', templateId: 'tpl', name, location, notes: null, isActive: true };
}

function status(
  assetId: string,
  value: AssetStatusRow['status'],
  extra: Partial<AssetStatusRow> = {},
): AssetStatusRow {
  const live = value === 'healthy';
  return {
    assetId,
    status: value,
    since: live ? null : '2026-11-24T11:50:00Z',
    alertId: live ? null : `alert-${assetId}`,
    severity: value === 'alert' ? 'critical' : value === 'healthy' ? null : 'warn',
    lastSeenAt: '2026-11-24T11:59:00Z',
    isActive: true,
    ...extra,
  };
}

function setup(): {
  fixture: ComponentFixture<DashboardPage>;
  http: HttpTestingController;
  page: DashboardPage;
  socket: {
    state: WritableSignal<RealtimeState>;
    start: ReturnType<typeof vi.fn>;
    stop: ReturnType<typeof vi.fn>;
    handlers: () => DashboardSocketHandlers;
  };
} {
  const state = signal<RealtimeState>('live');
  let handlers: DashboardSocketHandlers | null = null;
  const socket = {
    state,
    start: vi.fn((next: DashboardSocketHandlers) => {
      handlers = next;
    }),
    stop: vi.fn(),
    handlers: () => handlers!,
  };

  TestBed.configureTestingModule({
    imports: [DashboardPage],
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      ...provideTestTransloco(),
      { provide: DashboardSocket, useValue: socket },
    ],
  });

  TestBed.inject(AuthService).apply(SESSION);
  const http = TestBed.inject(HttpTestingController);
  const fixture = TestBed.createComponent(DashboardPage);
  return { fixture, http, page: fixture.componentInstance, socket };
}

function flushSnapshot(
  http: HttpTestingController,
  statuses: AssetStatusRow[],
  assets: AssetRow[],
): void {
  http.expectOne((request) => request.url === '/api/v1/assets/status').flush(statuses);
  http.expectOne((request) => request.url === '/api/v1/assets').flush(assets);
}

const FOUR = [
  status('a1', 'healthy'),
  status('a2', 'warning'),
  status('a3', 'alert'),
  status('a4', 'offline'),
];

const FOUR_ASSETS = [
  asset('a1', 'Healthy room'),
  asset('a2', 'Warm room'),
  asset('a3', 'Critical room'),
  asset('a4', 'Silent room'),
];

describe('DashboardPage', () => {
  afterEach(() => {
    vi.useRealTimers();
    TestBed.inject(HttpTestingController).verify();
  });

  it('shows the server’s four states as label + icon, never colour alone', async () => {
    const { fixture, http } = setup();
    fixture.detectChanges();
    flushSnapshot(http, FOUR, FOUR_ASSETS);
    await fixture.whenStable();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const text = host.textContent ?? '';
    expect(text).toContain('Healthy');
    expect(text).toContain('Warning');
    expect(text).toContain('Alert');
    expect(text).toContain('Offline');
    expect(text).not.toContain('Muted');

    const icons = [...host.querySelectorAll('[data-status-icon]')];
    expect(icons.length).toBe(4);
    expect(icons.map((icon) => icon.getAttribute('data-status-icon'))).toEqual([
      'healthy',
      'warning',
      'alert',
      'offline',
    ]);

    // the pill carries the derived state as data — a stylesheet alone cannot lie
    const pills = [...host.querySelectorAll('.dash-pill')].map((pill) =>
      pill.getAttribute('data-status'),
    );
    expect(pills).toEqual(['healthy', 'warning', 'alert', 'offline']);
  });

  it('filters to problems and keeps every non-healthy state', async () => {
    const { fixture, http, page } = setup();
    fixture.detectChanges();
    flushSnapshot(http, FOUR, FOUR_ASSETS);
    await fixture.whenStable();
    fixture.detectChanges();

    page.filter.set('problems');
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.textContent).not.toContain('Healthy room');
    expect(host.textContent).toContain('Warm room');
    expect(host.textContent).toContain('Critical room');
    expect(host.textContent).toContain('Silent room');
  });

  it('refetches the snapshot when the socket reconnects, and the alert hint changes no state', async () => {
    const { fixture, http, page, socket } = setup();
    fixture.detectChanges();
    flushSnapshot(http, FOUR, FOUR_ASSETS);
    await fixture.whenStable();

    const before = page.statuses();

    // the alerts channel is a hint for spec 05; it must not fabricate a status here.
    // The envelope is `{kind, alert}` (B10) — the page ignores it today.
    socket.handlers().onAlert({
      kind: 'changed',
      alert: {
        alertId: 'x',
        assetId: 'a2',
        severity: 'critical',
        status: 'open',
        since: '2026-11-24T12:00:00Z',
      },
    });
    expect(page.statuses()).toBe(before);

    // a reconnect is a missed-event window: the snapshot is fetched again (B5)
    socket.handlers().onResync();
    flushSnapshot(http, [status('a1', 'alert')], [asset('a1', 'Healthy room')]);
    await fixture.whenStable();
    expect(page.statuses()['a1'].status).toBe('alert');
  });

  it('refetches every 60 s while visible and stays quiet while hidden', async () => {
    vi.useFakeTimers();
    const { fixture, http } = setup();
    fixture.detectChanges();
    flushSnapshot(http, FOUR, FOUR_ASSETS);

    vi.advanceTimersByTime(60_000);
    flushSnapshot(http, FOUR, FOUR_ASSETS);

    const descriptor = Object.getOwnPropertyDescriptor(Document.prototype, 'visibilityState');
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'hidden' });

    try {
      vi.advanceTimersByTime(120_000);
      http.expectNone((request) => request.url === '/api/v1/assets/status');
    } finally {
      if (descriptor) Object.defineProperty(Document.prototype, 'visibilityState', descriptor);
      else delete (document as unknown as Record<string, unknown>)['visibilityState'];
    }
  });

  it('coalesces a burst of pushes into a few renders, not one per event', async () => {
    vi.useFakeTimers();
    const { fixture, http, page } = setup();
    fixture.detectChanges();
    flushSnapshot(http, FOUR, FOUR_ASSETS);

    const updates = vi.spyOn(page.statuses, 'update');

    for (let i = 0; i < 50; i++) {
      page.applyStatus(status('a2', i % 2 === 0 ? 'warning' : 'alert'));
      vi.advanceTimersByTime(2); // 50 events over 100 ms
    }
    vi.advanceTimersByTime(50);

    expect(updates.mock.calls.length).toBeGreaterThan(0);
    expect(updates.mock.calls.length).toBeLessThanOrEqual(4);
  });

  it('says “reconnecting” while showing the last snapshot it has', async () => {
    const { fixture, http, socket } = setup();
    fixture.detectChanges();
    flushSnapshot(http, FOUR, FOUR_ASSETS);
    await fixture.whenStable();

    socket.state.set('reconnecting');
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('.dash-link')?.getAttribute('data-state')).toBe('reconnecting');
    expect(host.textContent).toContain('Reconnecting');
    // the cards are still there: a dropped socket is a lost hint, not a blank screen
    expect(host.textContent).toContain('Critical room');
  });

  it('ages “last seen” from the server timestamp and never freshens it', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-11-24T12:00:00Z'));

    // hidden tab: the 60 s snapshot refetch pauses, the ageing clock does not
    const descriptor = Object.getOwnPropertyDescriptor(Document.prototype, 'visibilityState');
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'hidden' });

    try {
      const { fixture, http } = setup();
      fixture.detectChanges();
      flushSnapshot(
        http,
        [status('a1', 'healthy', { lastSeenAt: '2026-11-24T11:55:00Z' })],
        [asset('a1', 'Cold room')],
      );
      fixture.detectChanges();

      expect((fixture.nativeElement as HTMLElement).textContent).toContain('seen 5 min ago');

      vi.advanceTimersByTime(30_000 * 10);
      fixture.detectChanges();
      expect((fixture.nativeElement as HTMLElement).textContent).toContain('seen 10 min ago');
    } finally {
      if (descriptor) Object.defineProperty(Document.prototype, 'visibilityState', descriptor);
      else delete (document as unknown as Record<string, unknown>)['visibilityState'];
    }
  });

  it('says “no readings yet” for an asset the server never heard', async () => {
    const { fixture, http } = setup();
    fixture.detectChanges();
    flushSnapshot(
      http,
      [status('a1', 'healthy', { lastSeenAt: null })],
      [asset('a1', 'Never heard')],
    );
    await fixture.whenStable();
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('no readings yet');
  });

  it('clears a stale error banner when the next refresh succeeds', async () => {
    const { fixture, http, page } = setup();
    fixture.detectChanges();

    http
      .expectOne((request) => request.url === '/api/v1/assets/status')
      .flush({ code: 'assets:asset_not_found' }, { status: 404, statusText: 'Not Found' });
    // forkJoin cancels the sibling request; `match` drops it from the open list
    // (flushing a cancelled request throws).
    http.match((request) => request.url === '/api/v1/assets');
    fixture.detectChanges();
    expect(page.error()).not.toBeNull();

    // A transient failure is not a permanent screen: the next honest read clears it.
    page.refresh();
    flushSnapshot(http, FOUR, FOUR_ASSETS);
    await fixture.whenStable();
    fixture.detectChanges();

    expect(page.error()).toBeNull();
    expect((fixture.nativeElement as HTMLElement).textContent).not.toContain(
      'no longer exists',
    );
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Critical room');
  });

  it('never turns an unknown server status into a NaN count', async () => {
    const { fixture, http } = setup();
    fixture.detectChanges();

    // A value outside the four words must be ignored, not counted as `undefined`.
    const bogus = { ...status('a1', 'healthy'), status: 'muted' } as unknown as AssetStatusRow;
    flushSnapshot(http, [bogus], [asset('a1', 'Odd room')]);
    await fixture.whenStable();
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).not.toContain('NaN');
    expect(text).toContain('0 healthy');
  });

  it('rolls a long silence up to hours and days, never “4320 min ago”', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-11-24T12:00:00Z'));

    const { fixture, http } = setup();
    fixture.detectChanges();
    flushSnapshot(
      http,
      [
        status('a1', 'healthy', { lastSeenAt: '2026-11-24T09:00:00Z' }), // 3 h
        status('a2', 'offline', { lastSeenAt: '2026-11-19T12:00:00Z' }), // 5 d
      ],
      [asset('a1', 'Hours room'), asset('a2', 'Days room')],
    );
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('seen 3 h ago');
    expect(text).toContain('seen 5 d ago');
    expect(text).not.toContain('180 min');
  });

  it('re-renders the summary on a language switch, without a reload', async () => {
    const { fixture, http } = setup();
    fixture.detectChanges();
    flushSnapshot(http, FOUR, FOUR_ASSETS);
    await fixture.whenStable();
    fixture.detectChanges();

    const text = (): string => (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text()).toContain('1 healthy · 1 warning · 1 alert · 1 offline');

    TestBed.inject(TranslocoService).setActiveLang('ar');
    await fixture.whenStable();
    fixture.detectChanges();

    // The summary is built in TS (`translate`), not by the pipe — it must still
    // follow the language signal, or it stays English until F5.
    expect(text()).toContain('1 سليم · 1 تحذير · 1 إنذار · 1 غير متصل');
    expect(text()).not.toContain('1 healthy');
  });

  it('keeps the connection the page owns, and stops it on destroy', async () => {
    const { fixture, http, socket } = setup();
    fixture.detectChanges();
    flushSnapshot(http, FOUR, FOUR_ASSETS);

    expect(socket.start).toHaveBeenCalledTimes(1);
    fixture.destroy();
    expect(socket.stop).toHaveBeenCalledTimes(1);
  });
});
