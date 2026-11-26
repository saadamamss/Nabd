import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { TranslocoService } from '@jsverse/transloco';
import { AlertEvent, AlertRow } from '../../core/api/alerts.service';
import { AssetRow, UserRow } from '../../core/api/tenant-admin.service';
import { AuthService } from '../../core/auth/auth.service';
import { NoticeService } from '../../core/ui/notice.service';
import { provideTestTransloco } from '../../testing/test-transloco';
import { DICTIONARIES } from '../../testing/test-dictionaries';
import { routes } from '../../app.routes';
import { AlertDetailPage } from './alert-detail';
import {
  actorNameOf,
  eventKey,
  evidenceFacts,
  reasonKey,
  reasonTooLong,
  RESOLVE_REASON_MAX,
  resolveReasonOf,
} from './alerts-model';

// P5 spec 05, S3a — detail + evidence + the quiet-hours badge, RED first.
// S3b adds the timeline: the door's order rendered as received, actor names from
// the users list, and reason codes spoken while free text stays as written.
//
// The net pins the ways this page can lie: evidence re-read from the rule instead
// of the stored snapshot, a quiet-hours badge that depends on the status (so an
// ack "clears" a mute that really happened), and a badge fed by the events door —
// which is S3b's read and must not exist here.

const ASSET_ID = 'a3f1c2d4-5b6e-4f70-8a91-0b2c3d4e5f60';
const ALERT_ID = '12ac9443-3507-43a2-9bda-f5e500400b27';
const VIEWER = 'dc8c8785-e324-4f02-a8c4-d9f274fa5605';
const ADMIN = 'b2b7a1a0-9c1e-4c1e-9a35-8d1e0b1f2c3d';

function asset(assetId: string, name: string): AssetRow {
  return { assetId, tenantId: 't1', templateId: 'tpl', name, location: null, notes: null, isActive: true };
}

function user(userId: string, email: string, displayName?: string | null): UserRow {
  return { userId, tenantId: 't1', email, role: 'viewer', locale: 'en', displayName };
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

describe('alert timeline vocabulary', () => {
  it('names every lifecycle event the audit table can hold', () => {
    expect(eventKey('opened')).toBe('alerts.eventOpened');
    expect(eventKey('reopened')).toBe('alerts.eventReopened');
    expect(eventKey('acknowledged')).toBe('alerts.eventAcknowledged');
    expect(eventKey('resolved')).toBe('alerts.eventResolved');
    expect(eventKey('suppressed')).toBe('alerts.eventSuppressed');
    expect(eventKey('deduplicated')).toBe('alerts.eventDeduplicated');
    // a future event type is shown as received, never swallowed
    expect(eventKey('escalated')).toBeNull();
  });

  it('translates the engine reason codes and nothing else', () => {
    expect(reasonKey('mute_window')).toBe('alerts.reasonMuteWindow');
    expect(reasonKey('cooldown')).toBe('alerts.reasonCooldown');
    expect(reasonKey('condition_restored')).toBe('alerts.reasonConditionRestored');
    expect(reasonKey('device_online_restored')).toBe('alerts.reasonDeviceOnlineRestored');
    expect(reasonKey('manual')).toBe('alerts.reasonManual');
    // an admin's free text is history, not vocabulary: left exactly as stored
    expect(reasonKey('compressor replaced')).toBeNull();
    expect(reasonKey('')).toBeNull();
    expect(reasonKey(null)).toBeNull();
  });

  it('resolves an actor by display name, then email, and admits a missing one', () => {
    const users = [user(VIEWER, 'night@nabd.io', 'Night Shift'), user(ADMIN, 'ops@nabd.io', '  ')];

    expect(actorNameOf(users, VIEWER)).toBe('Night Shift');
    expect(actorNameOf(users, ADMIN)).toBe('ops@nabd.io');
    expect(actorNameOf(users, 'gone-forever')).toBeNull();
  });
});

describe('resolve reason model', () => {
  it('measures the text the way the server does: after the trim', () => {
    expect(resolveReasonOf('   ')).toBeUndefined();
    expect(resolveReasonOf('  compressor replaced  ')).toBe('compressor replaced');

    // trailing space can never push a 200-char sentence over the cap
    expect(reasonTooLong(`${'x'.repeat(200)}   `)).toBe(false);
    expect(reasonTooLong('x'.repeat(200))).toBe(false);
    expect(reasonTooLong('x'.repeat(201))).toBe(true);
    expect(RESOLVE_REASON_MAX).toBe(200);
  });
});

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

  /**
   * The page reads its asset names once, the alert once, then the trail and the
   * users it names — four reads, each with its own failure.
   */
  function load(row: AlertRow, events: AlertEvent[] = [], users: UserRow[] = []): void {
    http
      .expectOne((request) => request.url === '/api/v1/assets')
      .flush([asset(ASSET_ID, 'Cold room')]);
    http.expectOne((request) => request.url === `/api/v1/alerts/${ALERT_ID}`).flush(row);
    fixture.detectChanges();
    http.expectOne((request) => request.url === `/api/v1/alerts/${ALERT_ID}/events`).flush({ items: events });
    http.expectOne((request) => request.url === '/api/v1/tenants/t1/users').flush(users);
    fixture.detectChanges();
  }

  const eventsOnScreen = (): string[] =>
    Array.from(dom().querySelectorAll('[data-event]')).map(
      (node) => node.getAttribute('data-event') ?? '',
    );

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

  it('draws the history in the door order and never re-sorts it', () => {
    // deliberately "wrong" chronologically: the door's order is the truth
    load(
      alert({ status: 'resolved', resolvedAt: '2026-11-24T13:30:00Z' }),
      [
        { event: 'resolved', at: '2026-11-24T13:30:00Z', actorUserId: ADMIN, reason: 'condition_restored' },
        { event: 'acknowledged', at: '2026-11-24T12:05:00Z', actorUserId: VIEWER },
        { event: 'opened', at: '2026-11-24T11:50:00Z' },
      ],
      [user(VIEWER, 'night@nabd.io', 'Night Shift'), user(ADMIN, 'ops@nabd.io', 'Ops Admin')],
    );

    expect(eventsOnScreen()).toEqual(['resolved', 'acknowledged', 'opened']);
    expect(text()).toContain('Ops Admin');
    expect(text()).toContain('Night Shift');
    // the code is spoken, not printed
    expect(text()).toContain('Condition restored');
    expect(text()).not.toContain('condition_restored');
  });

  it('leaves an admin\'s free text and an unknown code exactly as stored', () => {
    load(alert(), [
      { event: 'resolved', at: '2026-11-24T13:30:00Z', reason: 'compressor replaced' },
      { event: 'escalated', at: '2026-11-24T14:00:00Z', reason: 'brand_new_code' },
    ]);

    expect(text()).toContain('compressor replaced');
    expect(text()).toContain('brand_new_code');
    // a future event type and an unknown code still render — never a blank row
    expect(eventsOnScreen()).toEqual(['resolved', 'escalated']);
  });

  it('says "a user" when the actor is gone, never a blank cell', () => {
    load(alert(), [
      { event: 'acknowledged', at: '2026-11-24T12:05:00Z', actorUserId: 'gone-forever' },
    ]);

    expect(dom().querySelector('.alert-event-actor')?.textContent).toContain('A user');
  });

  it('says so when the history read fails, and keeps the alert on screen', () => {
    http.expectOne((request) => request.url === '/api/v1/assets').flush([]);
    http.expectOne((request) => request.url === `/api/v1/alerts/${ALERT_ID}`).flush(alert());
    fixture.detectChanges();
    http.expectOne((request) => request.url === '/api/v1/tenants/t1/users').flush([]);
    http
      .expectOne((request) => request.url === `/api/v1/alerts/${ALERT_ID}/events`)
      .flush({ code: 'alerting:alert_not_found' }, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    expect(text()).toContain('Freezer too warm');
    expect(text()).toContain('no longer exists');
    expect(page.timelineProblem()?.key).toBe('errors.notFound');
  });

  it('says the trail is empty instead of drawing nothing', () => {
    load(alert());

    expect(text()).toContain('No events recorded');
    expect(eventsOnScreen()).toEqual([]);
  });

  it('shows a loading line before the answer', () => {
    expect(text()).toContain('Loading…');
    http.match(() => true);
  });

  // ===== S4 — the two actions =====

  /** The session is what gates the buttons; a role is the only thing it decides. */
  function signIn(role: string): void {
    TestBed.inject(AuthService).apply({
      user: { userId: VIEWER, tenantId: 't1', email: 'me@nabd.io', role, locale: 'en' },
      accessToken: 'test-token',
    });
  }

  const ask = (selector: string): void =>
    (dom().querySelector(selector) as HTMLButtonElement).click();

  const resolveUrl = `/api/v1/alerts/${ALERT_ID}/resolve`;
  const eventsUrl = `/api/v1/alerts/${ALERT_ID}/events`;

  it('lets a viewer acknowledge, and never offers resolve', () => {
    signIn('viewer');
    load(alert(), [], [user(VIEWER, 'night@nabd.io', 'Night Shift')]);

    expect(dom().querySelector('[data-action="ack"]')).not.toBeNull();
    expect(dom().querySelector('[data-action="resolve"]')).toBeNull();

    ask('[data-action="ack"]');
    fixture.detectChanges();

    const post = http.expectOne((request) => request.url === `/api/v1/alerts/${ALERT_ID}/ack`);
    expect(post.request.method).toBe('POST');
    post.flush(
      alert({
        status: 'acknowledged',
        acknowledgedAt: '2026-11-24T12:05:00Z',
        acknowledgedBy: VIEWER,
      }),
    );
    fixture.detectChanges();

    // the row the door wrote is drawn, and the trail it appended is read again
    http
      .expectOne((request) => request.url === eventsUrl)
      .flush({ items: [{ event: 'acknowledged', at: '2026-11-24T12:05:00Z', actorUserId: VIEWER }] });
    fixture.detectChanges();

    expect(dom().querySelector('[data-status]')?.getAttribute('data-status')).toBe('acknowledged');
    expect(text()).toContain('Night Shift');
    expect(TestBed.inject(NoticeService).notices()[0].key).toBe('toast.alertAcknowledged');
    // an acknowledged alert has nothing left to acknowledge
    expect(dom().querySelector('[data-action="ack"]')).toBeNull();
  });

  it('draws no empty actions panel for a viewer on an acknowledged alert', () => {
    signIn('viewer');
    load(
      alert({
        status: 'acknowledged',
        acknowledgedAt: '2026-11-24T12:05:00Z',
        acknowledgedBy: VIEWER,
      }),
    );

    // there is no door for a viewer here, and no heading over an empty row either
    expect(dom().querySelector('[data-action="ack"]')).toBeNull();
    expect(dom().querySelector('[data-action="resolve"]')).toBeNull();
    expect(dom().querySelector('[data-actions]')).toBeNull();
  });

  it('offers an admin only resolve on an acknowledged alert', () => {
    signIn('tenant_admin');
    load(
      alert({
        status: 'acknowledged',
        acknowledgedAt: '2026-11-24T12:05:00Z',
        acknowledgedBy: VIEWER,
      }),
    );

    expect(dom().querySelector('[data-actions]')).not.toBeNull();
    expect(dom().querySelector('[data-action="ack"]')).toBeNull();
    expect(dom().querySelector('[data-action="resolve"]')).not.toBeNull();
  });

  it('resolves with no reason as an absent parameter — never an empty one', () => {
    signIn('tenant_admin');
    load(alert());

    ask('[data-action="resolve"]');
    fixture.detectChanges();
    expect(dom().querySelector('[aria-modal="true"]')).not.toBeNull();

    ask('[data-action="confirm-resolve"]');
    fixture.detectChanges();

    const post = http.expectOne((request) => request.url === resolveUrl);
    expect(post.request.method).toBe('POST');
    expect(post.request.params.has('reason')).toBe(false);
    post.flush(alert({ status: 'resolved', resolvedAt: '2026-11-24T13:30:00Z' }));
    fixture.detectChanges();

    http
      .expectOne((request) => request.url === eventsUrl)
      .flush({ items: [{ event: 'resolved', at: '2026-11-24T13:30:00Z', reason: 'manual' }] });
    fixture.detectChanges();

    expect(dom().querySelector('[data-status]')?.getAttribute('data-status')).toBe('resolved');
    expect(dom().querySelector('[aria-modal="true"]')).toBeNull();
    expect(TestBed.inject(NoticeService).notices()[0].key).toBe('toast.alertResolved');
    expect(dom().querySelector('[data-action="ack"]')).toBeNull();
  });

  it('counts the reason and sends it trimmed', () => {
    signIn('tenant_admin');
    load(alert());

    page.openResolve();
    fixture.detectChanges();

    const box = dom().querySelector('textarea') as HTMLTextAreaElement;
    box.value = '  compressor replaced  ';
    box.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    expect(dom().querySelector('[data-reason-counter]')?.textContent).toContain('19 / 200');

    ask('[data-action="confirm-resolve"]');
    fixture.detectChanges();

    const post = http.expectOne((request) => request.url === resolveUrl);
    expect(post.request.params.get('reason')).toBe('compressor replaced');
    post.flush(alert({ status: 'resolved', resolvedAt: '2026-11-24T13:30:00Z' }));
    fixture.detectChanges();
    http.expectOne((request) => request.url === eventsUrl).flush({ items: [] });
    fixture.detectChanges();
  });

  it('refuses a 201-character reason without asking the server', () => {
    signIn('tenant_admin');
    load(alert());

    page.openResolve();
    fixture.detectChanges();

    const box = dom().querySelector('textarea') as HTMLTextAreaElement;
    box.value = 'x'.repeat(201);
    box.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    expect(dom().querySelector('[data-reason-counter]')?.textContent).toContain('201 / 200');

    ask('[data-action="confirm-resolve"]');
    fixture.detectChanges();

    http.expectNone((request) => request.url.includes('/resolve'));
    expect(dom().querySelector('[data-reason-problem]')?.textContent).toContain('200 characters');
    expect(dom().querySelector('[aria-modal="true"]')).not.toBeNull();
  });

  it("anchors the server's invalid_reason to the field and keeps the dialog", () => {
    signIn('tenant_admin');
    load(alert());

    page.openResolve();
    fixture.detectChanges();

    ask('[data-action="confirm-resolve"]');
    fixture.detectChanges();

    http
      .expectOne((request) => request.url === resolveUrl)
      .flush({ code: 'alerting:invalid_reason' }, { status: 422, statusText: 'Unprocessable' });
    fixture.detectChanges();

    expect(dom().querySelector('[data-reason-problem]')?.textContent).toContain('200 characters');
    expect(dom().querySelector('[aria-modal="true"]')).not.toBeNull();
    // the field's refusal is not the action's
    expect(dom().querySelector('[data-action-problem]')).toBeNull();
  });

  it('says someone resolved first, then reads the alert again', () => {
    signIn('tenant_admin');
    load(alert());

    page.openResolve();
    fixture.detectChanges();

    ask('[data-action="confirm-resolve"]');
    fixture.detectChanges();

    http
      .expectOne((request) => request.url === resolveUrl)
      .flush({ code: 'alerting:invalid_status' }, { status: 422, statusText: 'Unprocessable' });
    fixture.detectChanges();

    expect(dom().querySelector('[aria-modal="true"]')).toBeNull();
    expect(dom().querySelector('[data-action-problem]')?.textContent).toContain('already resolved');

    // the truth is read again, never assumed
    http
      .expectOne((request) => request.url === `/api/v1/alerts/${ALERT_ID}`)
      .flush(alert({ status: 'resolved', resolvedAt: '2026-11-24T13:30:00Z' }));
    fixture.detectChanges();
    http.expectOne((request) => request.url === eventsUrl).flush({ items: [] });
    fixture.detectChanges();

    expect(dom().querySelector('[data-status]')?.getAttribute('data-status')).toBe('resolved');
  });

  it("refuses a forced resolve as a viewer, in the reader's language", () => {
    signIn('viewer');
    load(alert());

    page.resolve();
    fixture.detectChanges();

    http.expectNone((request) => request.url.includes('/resolve'));
    expect(page.actionProblem()?.key).toBe('errors.forbidden');
    expect(dom().querySelector('[data-action-problem]')?.textContent).toContain(
      'role does not allow',
    );
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
    'alerts.timeline',
    'alerts.noEvents',
    'alerts.someUser',
    'alerts.eventOpened',
    'alerts.eventReopened',
    'alerts.eventAcknowledged',
    'alerts.eventResolved',
    'alerts.eventSuppressed',
    'alerts.eventDeduplicated',
    'alerts.reasonMuteWindow',
    'alerts.reasonCooldown',
    'alerts.reasonConditionRestored',
    'alerts.reasonDeviceOnlineRestored',
    'alerts.reasonManual',
    'alerts.actions',
    'alerts.acknowledge',
    'alerts.acknowledging',
    'alerts.resolve',
    'alerts.resolving',
    'alerts.resolveTitle',
    'alerts.resolveBody',
    'alerts.resolveReason',
    'alerts.resolveReasonHint',
    'toast.alertAcknowledged',
    'toast.alertResolved',
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
