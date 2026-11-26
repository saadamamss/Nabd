import { Component, signal, WritableSignal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { TranslocoService } from '@jsverse/transloco';
import { describeApiError } from '../../core/api/api-error';
import { AlertRow } from '../../core/api/alerts.service';
import { AssetRow } from '../../core/api/tenant-admin.service';
import {
  DashboardSocket,
  DashboardSocketHandlers,
  RealtimeState,
} from '../../core/realtime/dashboard.socket';
import { provideTestTransloco } from '../../testing/test-transloco';
import { DICTIONARIES } from '../../testing/test-dictionaries';
import { routes } from '../../app.routes';
import { AlertsPage } from './alerts';
import {
  DEFAULT_FILTERS,
  QueryRead,
  applyHint,
  doorQuery,
  focusedAlertId,
  matchesFilters,
  placeRow,
  queryParams,
  readFilters,
  severityKey,
  statusKey,
  titleFor,
} from './alerts-model';

/** The detail route is S3a's page; this spec only needs its address to resolve. */
@Component({ selector: 'app-alert-detail-stub', template: '' })
class DetailStub {}

// P5 spec 05, S2 — the list + filters + URL state, RED first.
//
// The net pins the three ways this page can lie: a filter that reaches the door
// as `all`, a filter that only applies on the first load (back/forward dead), and
// a title that ignores the locale. The rendering facts are checked through the
// DOM; the mapping facts through the pure model.

function query(values: Record<string, string>): QueryRead {
  return { get: (name: string) => values[name] ?? null };
}

const ASSET_ID = 'a3f1c2d4-5b6e-4f70-8a91-0b2c3d4e5f60';
const ALERT_ID = 'c46fd719-b725-4630-be85-b46d5d85df37';

function asset(assetId: string, name: string): AssetRow {
  return { assetId, tenantId: 't1', templateId: 'tpl', name, location: null, notes: null, isActive: true };
}

function alert(overrides: Partial<AlertRow> = {}): AlertRow {
  return {
    alertId: 'c46fd719-b725-4630-be85-b46d5d85df37',
    tenantId: 't1',
    assetId: ASSET_ID,
    ruleId: 'r1',
    type: 'threshold',
    severity: 'critical',
    status: 'open',
    openedAt: '2026-11-24T11:50:00Z',
    titleEn: 'Freezer too warm',
    titleAr: 'المجمّد أكثر حرارة من اللازم',
    detail: { ruleType: 'threshold', deviceId: 'd1', value: 12, channelKey: 'temp' },
    ...overrides,
  };
}

describe('alerts filter model', () => {
  it('maps the UI filters to the door’s parameters, and `all` is never sent', () => {
    expect(doorQuery(DEFAULT_FILTERS)).toEqual({
      status: undefined,
      severity: undefined,
      assetId: undefined,
      limit: 100,
    });

    expect(doorQuery({ status: 'open', severity: 'critical', assetId: ASSET_ID, limit: 500 })).toEqual({
      status: 'open',
      severity: 'critical',
      assetId: ASSET_ID,
      limit: 500,
    });
  });

  it('reads a URL back and drops anything the door never offered', () => {
    expect(readFilters(query({}))).toEqual(DEFAULT_FILTERS);

    expect(
      readFilters(query({ status: 'open', severity: 'critical', assetId: ASSET_ID, limit: '500' })),
    ).toEqual({ status: 'open', severity: 'critical', assetId: ASSET_ID, limit: 500 });

    // a hand-edited address must not reach the server as an invented filter
    expect(
      readFilters(query({ status: 'muted', severity: 'fatal', assetId: 'not-a-guid', limit: '7' })),
    ).toEqual(DEFAULT_FILTERS);
  });

  it('writes only the filters the user actually set', () => {
    expect(queryParams(DEFAULT_FILTERS)).toEqual({});
    expect(queryParams({ ...DEFAULT_FILTERS, status: 'resolved' })).toEqual({ status: 'resolved' });
    expect(queryParams({ ...DEFAULT_FILTERS, assetId: ASSET_ID, limit: 500 })).toEqual({
      assetId: ASSET_ID,
      limit: '500',
    });
  });

  it('forwards the dashboard’s focused alert, and ignores a broken id', () => {
    expect(focusedAlertId(query({ alertId: ALERT_ID }))).toBe(ALERT_ID);
    expect(focusedAlertId(query({ alertId: 'not-a-guid' }))).toBeNull();
    expect(focusedAlertId(query({}))).toBeNull();
  });

  it('names the locales it knows and shows the rest as received', () => {
    expect(titleFor({ titleEn: 'Warm', titleAr: 'حار' }, 'ar')).toBe('حار');
    expect(titleFor({ titleEn: 'Warm', titleAr: 'حار' }, 'en')).toBe('Warm');
    expect(statusKey('acknowledged')).toBe('alerts.statusAcknowledged');
    expect(statusKey('muted')).toBeNull();
    expect(severityKey('info')).toBe('alerts.severityInfo');
    expect(severityKey('fatal')).toBeNull();
  });
});

describe('alerts live model', () => {
  it('matches the active filter the way the door would', () => {
    const row = alert();

    expect(matchesFilters(row, DEFAULT_FILTERS)).toBe(true);
    expect(matchesFilters(row, { ...DEFAULT_FILTERS, status: 'open' })).toBe(true);
    expect(matchesFilters(row, { ...DEFAULT_FILTERS, status: 'resolved' })).toBe(false);
    expect(matchesFilters(row, { ...DEFAULT_FILTERS, severity: 'warn' })).toBe(false);
    expect(matchesFilters(row, { ...DEFAULT_FILTERS, assetId: 'some-other-asset' })).toBe(false);
  });

  it('places a door row where the door would: opened_at desc, capped, never twice', () => {
    const older = alert({ alertId: 'older', openedAt: '2026-11-24T08:00:00Z' });
    const middle = alert({ alertId: 'middle', openedAt: '2026-11-24T10:00:00Z' });
    const newest = alert({ alertId: 'newest', openedAt: '2026-11-24T12:00:00Z' });
    const rows = [middle, older];

    expect(placeRow(rows, newest, 100).map((row) => row.alertId)).toEqual([
      'newest',
      'middle',
      'older',
    ]);

    // the same id is replaced in place, not duplicated — a push can arrive twice
    const acked = alert({ alertId: 'middle', openedAt: '2026-11-24T10:00:00Z', status: 'acknowledged' });
    const placed = placeRow(rows, acked, 100);
    expect(placed.length).toBe(2);
    expect(placed.find((row) => row.alertId === 'middle')?.status).toBe('acknowledged');

    // the page's cap is the door's cap (E5), not an invented one
    expect(placeRow(rows, newest, 1).map((row) => row.alertId)).toEqual(['newest']);
  });

  it('moves only the pushed row, and only the facts the hint carries', () => {
    const untouched = alert({ alertId: 'other', status: 'resolved' });
    const rows = [alert(), untouched];

    const moved = applyHint(rows, {
      alertId: ALERT_ID,
      assetId: ASSET_ID,
      severity: 'warn',
      status: 'acknowledged',
      // the trigger's `since` is the row's `opened_at`, so a reopen moves it too
      since: '2026-11-24T12:05:00Z',
    });

    expect(moved[0].status).toBe('acknowledged');
    expect(moved[0].severity).toBe('warn');
    expect(moved[0].openedAt).toBe('2026-11-24T12:05:00Z');
    expect(moved[1]).toBe(untouched);
    // the hint has no ack time: it must not appear until the door's row lands
    expect('acknowledgedAt' in moved[0]).toBe(false);
  });
});

describe('AlertsPage', () => {
  let http: HttpTestingController;
  let fixture: ComponentFixture<AlertsPage>;
  let page: AlertsPage;
  let destroyed: boolean;

  const socketState = signal<RealtimeState>('live');
  let handlers: DashboardSocketHandlers | null = null;
  const socket = {
    state: socketState as WritableSignal<RealtimeState>,
    start: vi.fn((next: DashboardSocketHandlers) => {
      handlers = next;
    }),
    stop: vi.fn(),
  };

  const text = (): string => (fixture.nativeElement as HTMLElement).textContent ?? '';
  const rowStatus = (): string | null =>
    (fixture.nativeElement as HTMLElement)
      .querySelector('tr[data-status]')
      ?.getAttribute('data-status') ?? null;

  beforeEach(() => {
    handlers = null;
    destroyed = false;
    socketState.set('live');
    socket.start.mockClear();
    socket.stop.mockClear();

    TestBed.configureTestingModule({
      imports: [AlertsPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([
          { path: 'alerts', component: AlertsPage },
          { path: 'alerts/:alertId', component: DetailStub },
        ]),
        ...provideTestTransloco(),
        { provide: DashboardSocket, useValue: socket },
      ],
    });

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(AlertsPage);
    page = fixture.componentInstance;
    fixture.detectChanges();
  });

  afterEach(() => {
    vi.useRealTimers();
    http.verify();
    if (!destroyed) fixture.destroy();
  });

  /** Assets load once; alerts load on every filter change. */
  function flushAssets(): void {
    http
      .expectOne((request) => request.url === '/api/v1/assets')
      .flush([asset(ASSET_ID, 'Cold room')]);
  }

  function flushAlerts(rows: AlertRow[]): void {
    http.expectOne((request) => request.url === '/api/v1/alerts').flush(rows);
  }

  it('asks the door for the latest 100 and renders the row with its asset name', async () => {
    flushAssets();
    const request = http.expectOne((r) => r.url === '/api/v1/alerts');
    expect(request.request.params.has('status')).toBe(false);
    expect(request.request.params.has('severity')).toBe(false);
    expect(request.request.params.has('assetId')).toBe(false);
    expect(request.request.params.get('limit')).toBe('100');
    request.flush([alert()]);

    await fixture.whenStable();
    fixture.detectChanges();

    expect(text()).toContain('Freezer too warm');
    expect(text()).toContain('Cold room');
    expect(text()).toContain('Critical');
    expect(text()).toContain('Open');
    // the ack column is honest about nothing having happened
    expect(text()).toContain('—');
  });

  it('marks a muted episode on its row, whatever the status', async () => {
    flushAssets();
    flushAlerts([
      alert({ alertId: '11111111-1111-4111-8111-111111111111', quietHoursSuppressed: true }),
      alert({ alertId: ALERT_ID, status: 'acknowledged', quietHoursSuppressed: true }),
      alert({ alertId: '22222222-2222-4222-8222-222222222222', quietHoursSuppressed: false }),
    ]);
    await fixture.whenStable();
    fixture.detectChanges();

    expect(text()).toContain('Not notified');
    const badges = (fixture.nativeElement as HTMLElement).querySelectorAll('[data-quiet-hours]');
    expect(badges.length).toBe(2);
  });

  it('navigates with the filter, and the door receives exactly that filter', async () => {
    flushAssets();
    flushAlerts([alert()]);

    page.setStatus('open');
    await fixture.whenStable();
    let request = http.expectOne((r) => r.url === '/api/v1/alerts');
    expect(request.request.params.get('status')).toBe('open');
    expect(request.request.params.has('severity')).toBe(false);
    request.flush([alert()]);

    page.setSeverity('critical');
    await fixture.whenStable();
    request = http.expectOne((r) => r.url === '/api/v1/alerts');
    expect(request.request.params.get('status')).toBe('open');
    expect(request.request.params.get('severity')).toBe('critical');
    request.flush([alert()]);

    expect(TestBed.inject(Router).url).toBe('/?status=open&severity=critical');
  });

  it('follows the URL both ways — a pasted link and a later navigation both refetch', async () => {
    flushAssets();
    flushAlerts([alert()]);

    const router = TestBed.inject(Router);
    await router.navigateByUrl('/alerts?status=acknowledged&severity=warn');
    await fixture.whenStable();

    let request = http.expectOne((r) => r.url === '/api/v1/alerts');
    expect(request.request.params.get('status')).toBe('acknowledged');
    expect(request.request.params.get('severity')).toBe('warn');
    request.flush([alert({ status: 'acknowledged' })]);

    // back/forward is the same road: the URL changes, the page follows
    await router.navigateByUrl('/alerts?status=resolved');
    await fixture.whenStable();

    request = http.expectOne((r) => r.url === '/api/v1/alerts');
    expect(request.request.params.get('status')).toBe('resolved');
    expect(request.request.params.has('severity')).toBe(false);
    request.flush([alert({ status: 'resolved' })]);
  });

  it('says which empty it is, and clears the filters back to a plain address', async () => {
    flushAssets();
    flushAlerts([]);
    await fixture.whenStable();
    fixture.detectChanges();
    expect(text()).toContain('No alerts yet');

    page.setStatus('open');
    await fixture.whenStable();
    flushAlerts([]);
    await fixture.whenStable();
    fixture.detectChanges();
    expect(text()).toContain('No alerts match these filters');

    page.reset();
    await fixture.whenStable();
    const request = http.expectOne((r) => r.url === '/api/v1/alerts');
    expect(request.request.params.has('status')).toBe(false);
    request.flush([]);
    expect(TestBed.inject(Router).url).toBe('/');
  });

  it('picks the title by locale, without a reload', async () => {
    flushAssets();
    flushAlerts([alert()]);
    await fixture.whenStable();
    fixture.detectChanges();
    expect(text()).toContain('Freezer too warm');

    TestBed.inject(TranslocoService).setActiveLang('ar');
    await fixture.whenStable();
    fixture.detectChanges();

    expect(text()).toContain('المجمّد أكثر حرارة من اللازم');
    expect(text()).not.toContain('Freezer too warm');
    expect(text()).toContain('حرِج');
  });

  it('renders a refused filter through the error map', async () => {
    flushAssets();
    http
      .expectOne((r) => r.url === '/api/v1/alerts')
      .flush({ code: 'alerting:invalid_filter' }, { status: 422, statusText: 'Unprocessable Entity' });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(page.problem()?.key).toBe('errors.alertFilter');
    expect(text()).toContain('That filter is not valid.');
  });

  it('forwards ?alertId= to the alert’s own address instead of ignoring it', async () => {
    flushAssets();
    flushAlerts([alert()]);

    const router = TestBed.inject(Router);
    await router.navigateByUrl(`/alerts?alertId=${ALERT_ID}`);
    await fixture.whenStable();

    expect(router.url).toBe(`/alerts/${ALERT_ID}`);
    // the redirect leaves this page; any read it started is not the list's
    http.match(() => true);
  });

  it('shows a loading line before the first answer', () => {
    expect(text()).toContain('Loading…');
    // the answer is irrelevant here; `match` drops both in-flight reads so verify() stays quiet
    http.match(() => true);
  });

  it('moves a row from the hint at once, then takes the rest from the door', async () => {
    flushAssets();
    flushAlerts([alert()]);
    await fixture.whenStable();
    expect(rowStatus()).toBe('open');

    vi.useFakeTimers();

    handlers!.onAlert({
      kind: 'changed',
      alert: {
        alertId: ALERT_ID,
        assetId: ASSET_ID,
        severity: 'critical',
        status: 'acknowledged',
        since: '2026-11-24T11:50:00Z',
      },
    });

    // the committed status is on screen before any read — and the list is NOT reloaded
    fixture.detectChanges();
    expect(rowStatus()).toBe('acknowledged');
    http.expectNone((request) => request.url === '/api/v1/alerts');

    // the ack time exists only in the door's row, so it arrives afterwards
    vi.advanceTimersByTime(40);
    http
      .expectOne((request) => request.url === `/api/v1/alerts/${ALERT_ID}`)
      .flush(alert({ status: 'acknowledged', acknowledgedAt: '2026-11-24T12:10:00Z' }));

    fixture.detectChanges();
    expect(page.alerts().length).toBe(1);
    expect(page.alerts()[0].acknowledgedAt).toBe('2026-11-24T12:10:00Z');
    expect(rowStatus()).toBe('acknowledged');
  });

  it('prepends a raised alert that matches the filter, and drops one it excludes', async () => {
    flushAssets();
    flushAlerts([alert()]);
    await fixture.whenStable();

    page.setStatus('open');
    await fixture.whenStable();
    flushAlerts([alert()]);
    await fixture.whenStable();

    vi.useFakeTimers();

    const raised = alert({
      alertId: 'aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa',
      titleEn: 'Door left open',
      openedAt: '2026-11-24T12:20:00Z',
    });
    handlers!.onAlert({
      kind: 'raised',
      alert: {
        alertId: raised.alertId,
        assetId: raised.assetId,
        severity: raised.severity,
        status: raised.status,
        since: raised.openedAt,
      },
    });
    vi.advanceTimersByTime(40);
    http.expectOne((request) => request.url === `/api/v1/alerts/${raised.alertId}`).flush(raised);
    fixture.detectChanges();

    // the door's order is opened_at DESC, and the cap is the active one
    expect(page.alerts().map((row) => row.alertId)).toEqual([raised.alertId, ALERT_ID]);
    expect(text()).toContain('Door left open');

    // an alert the active status filter excludes never joins the list
    const resolved = alert({
      alertId: 'bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb',
      status: 'resolved',
      openedAt: '2026-11-24T12:30:00Z',
    });
    handlers!.onAlert({
      kind: 'raised',
      alert: {
        alertId: resolved.alertId,
        assetId: resolved.assetId,
        severity: resolved.severity,
        status: resolved.status,
        since: resolved.openedAt,
      },
    });
    vi.advanceTimersByTime(40);
    http.expectOne((request) => request.url === `/api/v1/alerts/${resolved.alertId}`).flush(resolved);
    fixture.detectChanges();

    expect(page.alerts().map((row) => row.alertId)).toEqual([raised.alertId, ALERT_ID]);
  });

  it('refetches every 30 s while the socket is not live, quietly and without a storm', () => {
    vi.useFakeTimers();

    // The page's interval must be created under the fake clock, so this test owns
    // a page mounted after the clock was swapped (the harness mounts with real ones).
    // The harness page's first reads are answered before it goes away.
    flushAssets();
    flushAlerts([alert()]);
    fixture.destroy();
    fixture = TestBed.createComponent(AlertsPage);
    page = fixture.componentInstance;
    fixture.detectChanges();

    flushAssets();
    flushAlerts([alert()]);

    socketState.set('reconnecting');

    vi.advanceTimersByTime(30_000);
    http.expectOne((request) => request.url === '/api/v1/alerts').flush([alert({ status: 'resolved' })]);
    fixture.detectChanges();
    expect(rowStatus()).toBe('resolved');

    // a failed stand-in read changes nothing: the rows stay, no banner appears
    vi.advanceTimersByTime(30_000);
    http
      .expectOne((request) => request.url === '/api/v1/alerts')
      .flush({ code: 'alerting:invalid_filter' }, { status: 422, statusText: 'Unprocessable Entity' });
    fixture.detectChanges();
    expect(page.problem()).toBeNull();
    expect(rowStatus()).toBe('resolved');

    // live again: the stand-in stands down
    socketState.set('live');
    vi.advanceTimersByTime(120_000);
    http.expectNone((request) => request.url === '/api/v1/alerts');
  });

  it('owns the socket while it is mounted and stops it on destroy', () => {
    flushAssets();
    flushAlerts([alert()]);

    expect(socket.start).toHaveBeenCalledTimes(1);
    expect(socket.start.mock.calls[0][0]).toBeTruthy();

    fixture.destroy();
    destroyed = true;
    expect(socket.stop).toHaveBeenCalledTimes(1);
  });
});

describe('alerts dictionaries, errors and route', () => {
  const keys = [
    'alerts.title',
    'alerts.body',
    'alerts.filterStatus',
    'alerts.filterSeverity',
    'alerts.filterAsset',
    'alerts.filterLimit',
    'alerts.filterAll',
    'alerts.filterAllSeverities',
    'alerts.filterAllAssets',
    'alerts.statusOpen',
    'alerts.statusAcknowledged',
    'alerts.statusResolved',
    'alerts.severityInfo',
    'alerts.severityWarn',
    'alerts.severityCritical',
    'alerts.colSeverity',
    'alerts.colStatus',
    'alerts.colAlert',
    'alerts.colAsset',
    'alerts.colOpened',
    'alerts.colAcknowledged',
    'alerts.empty',
    'alerts.emptyFiltered',
    'alerts.reset',
    'alerts.showingLatest',
    'alerts.assetUnknown',
    'errors.alertFilter',
    'errors.alertStatus',
    'errors.alertReason',
  ];

  it.each(keys)('%s exists in both dictionaries', (key) => {
    for (const lang of ['en', 'ar'] as const) {
      const leaf = key
        .split('.')
        .reduce<unknown>(
          (value, part) =>
            value && typeof value === 'object'
              ? (value as Record<string, unknown>)[part]
              : undefined,
          DICTIONARIES[lang],
        );

      expect(typeof leaf, `${lang}.${key} must be a string`).toBe('string');
      expect((leaf as string).trim().length, `${lang}.${key} must not be blank`).toBeGreaterThan(0);
    }
  });

  it('maps every alerting refusal to a dictionary key', () => {
    const cases: [string, string][] = [
      ['alerting:alert_not_found', 'errors.notFound'],
      ['alerting:asset_not_found', 'errors.notFound'],
      ['alerting:invalid_filter', 'errors.alertFilter'],
      ['alerting:invalid_status', 'errors.alertStatus'],
      ['alerting:invalid_reason', 'errors.alertReason'],
    ];

    for (const [code, key] of cases) {
      const problem = describeApiError(new HttpErrorResponse({ error: { code }, status: 422 }));
      expect(problem.key, code).toBe(key);
    }
  });

  it('replaces the alerts stub with the real page', () => {
    const shell = routes.find((route) => route.path === '');
    const child = shell?.children?.find((route) => route.path === 'alerts');

    expect(child?.component).toBe(AlertsPage);
  });
});
