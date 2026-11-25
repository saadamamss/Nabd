import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { TranslocoService } from '@jsverse/transloco';
import { describeApiError } from '../../core/api/api-error';
import { AlertRow } from '../../core/api/alerts.service';
import { AssetRow } from '../../core/api/tenant-admin.service';
import { provideTestTransloco } from '../../testing/test-transloco';
import { DICTIONARIES } from '../../testing/test-dictionaries';
import { routes } from '../../app.routes';
import { AlertsPage } from './alerts';
import {
  DEFAULT_FILTERS,
  QueryRead,
  doorQuery,
  queryParams,
  readFilters,
  severityKey,
  statusKey,
  titleFor,
} from './alerts-model';

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

  it('names the locales it knows and shows the rest as received', () => {
    expect(titleFor({ titleEn: 'Warm', titleAr: 'حار' }, 'ar')).toBe('حار');
    expect(titleFor({ titleEn: 'Warm', titleAr: 'حار' }, 'en')).toBe('Warm');
    expect(statusKey('acknowledged')).toBe('alerts.statusAcknowledged');
    expect(statusKey('muted')).toBeNull();
    expect(severityKey('info')).toBe('alerts.severityInfo');
    expect(severityKey('fatal')).toBeNull();
  });
});

describe('AlertsPage', () => {
  let http: HttpTestingController;
  let fixture: ComponentFixture<AlertsPage>;
  let page: AlertsPage;

  const text = (): string => (fixture.nativeElement as HTMLElement).textContent ?? '';

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [AlertsPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([{ path: 'alerts', component: AlertsPage }]),
        ...provideTestTransloco(),
      ],
    });

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(AlertsPage);
    page = fixture.componentInstance;
    fixture.detectChanges();
  });

  afterEach(() => {
    http.verify();
    fixture.destroy();
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

  it('shows a loading line before the first answer', () => {
    expect(text()).toContain('Loading…');
    // the answer is irrelevant here; `match` drops both in-flight reads so verify() stays quiet
    http.match(() => true);
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
