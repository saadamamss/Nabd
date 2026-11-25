import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { TranslocoService } from '@jsverse/transloco';
import { AlertRow } from '../../core/api/alerts.service';
import { AssetRow } from '../../core/api/tenant-admin.service';
import { provideTestTransloco } from '../../testing/test-transloco';
import { DICTIONARIES } from '../../testing/test-dictionaries';
import { routes } from '../../app.routes';
import { AlertDetailPage } from './alert-detail';
import { evidenceFacts } from './alerts-model';

// P5 spec 05, S3a — detail + evidence + the quiet-hours badge, RED first.
//
// The net pins the three ways this page can lie: evidence re-read from the rule
// instead of the stored snapshot, a quiet-hours badge that depends on the status
// (so an ack "clears" a mute that really happened), and a badge fed by the events
// door — which is S3b's read and must not exist here.

const ASSET_ID = 'a3f1c2d4-5b6e-4f70-8a91-0b2c3d4e5f60';
const ALERT_ID = '12ac9443-3507-43a2-9bda-f5e500400b27';

function asset(assetId: string, name: string): AssetRow {
  return { assetId, tenantId: 't1', templateId: 'tpl', name, location: null, notes: null, isActive: true };
}

function alert(overrides: Partial<AlertRow> = {}): AlertRow {
  return {
    alertId: ALERT_ID,
    tenantId: 't1',
    assetId: ASSET_ID,
    ruleId: 'r1',
    type: 'threshold',
    severity: 'critical',
    status: 'open',
    openedAt: '2026-11-24T11:50:00Z',
    titleEn: 'Freezer too warm',
    titleAr: 'المجمّد أكثر حرارة من اللازم',
    detail: {
      value: 12,
      channelKey: 'temp',
      ruleType: 'threshold',
      thresholdHigh: 9,
      deviceId: 'd1',
    },
    ...overrides,
  };
}

describe('alert evidence model', () => {
  it('renders the stored thresholds as they were, and never reconstructs them', () => {
    const facts = evidenceFacts(
      { value: 12, channelKey: 'temp', ruleType: 'threshold', thresholdHigh: 9, thresholdLow: 8, deviceId: 'd1' },
      'en',
    );

    expect(facts.map((fact) => fact.key)).toEqual([
      'alerts.evidenceValue',
      'alerts.evidenceThreshold',
      'alerts.evidenceChannel',
      'alerts.evidenceDevice',
    ]);
    expect(facts[0].value).toBe('12');
    expect(facts[1].value).toBe('8 – 9');
    expect(facts.every((fact) => fact.numeric)).toBe(true);
  });

  it('tells an offline alert by its silence, not by an invented reading', () => {
    const facts = evidenceFacts(
      {
        ruleType: 'offline',
        deviceId: 'd1',
        offlineGapMinutes: 61,
        lastSeenAtUtc: '2026-11-24T10:49:00Z',
      },
      'en',
    );

    expect(facts.map((fact) => fact.key)).toEqual([
      'alerts.evidenceDevice',
      'alerts.evidenceSilence',
      'alerts.evidenceLastSeen',
    ]);
    expect(facts[1].value).toBe('61');
    // the last-seen fact is a time, so it keeps the chrome's direction
    expect(facts[2].numeric).toBeUndefined();
  });

  it('has nothing to say about an absent detail', () => {
    expect(evidenceFacts(null, 'en')).toEqual([]);
    expect(evidenceFacts(undefined, 'en')).toEqual([]);
  });
});

describe('AlertDetailPage', () => {
  let http: HttpTestingController;
  let fixture: ComponentFixture<AlertDetailPage>;
  let page: AlertDetailPage;
  let params: BehaviorSubject<ReturnType<typeof convertToParamMap>>;

  const text = (): string => (fixture.nativeElement as HTMLElement).textContent ?? '';
  const dom = (): HTMLElement => fixture.nativeElement as HTMLElement;

  beforeEach(() => {
    params = new BehaviorSubject(convertToParamMap({ alertId: ALERT_ID }));

    TestBed.configureTestingModule({
      imports: [AlertDetailPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { paramMap: params } },
        ...provideTestTransloco(),
      ],
    });

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(AlertDetailPage);
    page = fixture.componentInstance;
    fixture.detectChanges();
  });

  afterEach(() => {
    http.verify();
    fixture.destroy();
  });

  /** The page reads its asset names once and the alert once. */
  function load(row: AlertRow): void {
    http
      .expectOne((request) => request.url === '/api/v1/assets')
      .flush([asset(ASSET_ID, 'Cold room')]);
    http.expectOne((request) => request.url === `/api/v1/alerts/${ALERT_ID}`).flush(row);
    fixture.detectChanges();
  }

  it('shows the evidence the alert stored, never a re-read rule', () => {
    load(alert());

    expect(text()).toContain('Freezer too warm');
    expect(text()).toContain('Cold room');
    expect(text()).toContain('Reading');
    expect(text()).toContain('12');
    expect(text()).toContain('Threshold at crossing');
    expect(text()).toContain('> 9');
    expect(text()).toContain('temp');
    // a later rule PATCH cannot rewrite the detail: no rule is asked at all
    http.expectNone((request) => request.url.includes('/rules'));
  });

  it('shows the quiet-hours badge and says why, on an open row', () => {
    load(alert({ quietHoursSuppressed: true }));

    expect(dom().querySelector('[data-quiet-hours]')).not.toBeNull();
    expect(text()).toContain('Not notified');
    expect(text()).toContain('nobody was notified');
    // the badge is a row fact (E9), not this page's events read
    http.expectNone((request) => request.url.includes('/events'));
    // and it does not turn the alert into anything else
    expect(dom().querySelector('[data-status]')?.getAttribute('data-status')).toBe('open');
  });

  it('keeps the badge after an acknowledgement — a mute really happened', () => {
    load(
      alert({
        quietHoursSuppressed: true,
        status: 'acknowledged',
        acknowledgedAt: '2026-11-24T12:05:00Z',
        acknowledgedBy: 'dc8c8785-e324-4f02-a8c4-d9f274fa5605',
      }),
    );

    expect(text()).toContain('Not notified');
    expect(dom().querySelector('[data-status]')?.getAttribute('data-status')).toBe('acknowledged');
    expect(text()).not.toContain('Not acknowledged');
  });

  it('keeps the badge on a resolved alert, and still says it was unmuted', () => {
    load(
      alert({
        quietHoursSuppressed: true,
        status: 'resolved',
        resolvedAt: '2026-11-24T13:30:00Z',
      }),
    );

    expect(text()).toContain('Not notified');
    expect(dom().querySelector('[data-status]')?.getAttribute('data-status')).toBe('resolved');
    expect(text()).not.toContain('Not resolved yet');
  });

  it('draws no badge when the episode was not muted', () => {
    load(alert());

    expect(dom().querySelector('[data-quiet-hours]')).toBeNull();
    expect(text()).not.toContain('Not notified');
  });

  it('is honest about the state it is in', () => {
    load(alert());

    expect(text()).toContain('Not acknowledged');
    expect(text()).toContain('Not resolved yet');
  });

  it('picks the title by locale and keeps the other language visible', async () => {
    load(alert());
    expect(dom().querySelector('.ui-title')?.textContent).toContain('Freezer too warm');
    expect(text()).toContain('Title (Arabic)');
    expect(text()).toContain('المجمّد أكثر حرارة من اللازم');

    TestBed.inject(TranslocoService).setActiveLang('ar');
    await fixture.whenStable();
    fixture.detectChanges();

    expect(dom().querySelector('.ui-title')?.textContent).toContain('المجمّد أكثر حرارة من اللازم');
    expect(text()).toContain('العنوان (بالإنجليزية)');
    expect(text()).toContain('Freezer too warm');
    expect(text()).toContain('حرِج');
  });

  it('says a gone alert is gone, and keeps the way back', () => {
    http
      .expectOne((request) => request.url === '/api/v1/assets')
      .flush([asset(ASSET_ID, 'Cold room')]);
    http
      .expectOne((request) => request.url === `/api/v1/alerts/${ALERT_ID}`)
      .flush({ code: 'alerting:alert_not_found' }, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    expect(page.problem()?.key).toBe('errors.notFound');
    expect(text()).toContain('no longer exists');
    expect(dom().querySelector('a[href="/alerts"]')).not.toBeNull();
  });

  it('shows a loading line before the answer', () => {
    expect(text()).toContain('Loading…');
    http.match(() => true);
  });
});

describe('alerts detail dictionary, errors and route', () => {
  const keys = [
    'alerts.backToList',
    'alerts.titleInAr',
    'alerts.titleInEn',
    'alerts.evidence',
    'alerts.evidenceValue',
    'alerts.evidenceThreshold',
    'alerts.evidenceChannel',
    'alerts.evidenceDevice',
    'alerts.evidenceSilence',
    'alerts.evidenceLastSeen',
    'alerts.noEvidence',
    'alerts.state',
    'alerts.acknowledgedAt',
    'alerts.resolvedAt',
    'alerts.notAcknowledged',
    'alerts.notResolved',
    'alerts.quietHours',
    'alerts.quietHoursBody',
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

  it('gives an alert its own address', () => {
    const shell = routes.find((route) => route.path === '');
    const child = shell?.children?.find((route) => route.path === 'alerts/:alertId');

    expect(child?.component).toBe(AlertDetailPage);
  });
});
