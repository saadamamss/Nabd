import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { provideTestTransloco } from '../../testing/test-transloco';
import { AuthService } from '../../core/auth/auth.service';
import { RulesPage } from './rules';

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

const TEMPLATE = {
  templateId: 'tpl-1',
  key: 'fridge',
  nameAr: 'ثلاجة',
  nameEn: 'Fridge',
  channels: [
    { key: 'temp', nameAr: 'الحرارة', nameEn: 'Temperature', unit: '°C', dataType: 'number', lowerLimit: 2, upperLimit: 8 },
  ],
};

const THRESHOLD = {
  ruleId: 'r1',
  tenantId: 't1',
  deviceId: 'd1',
  channelKey: 'temp',
  ruleType: 'outside_range',
  thresholdLow: 2,
  thresholdHigh: 8,
  durationSeconds: 60,
  cooldownMinutes: 60,
  severity: 'warn',
  enabled: true,
  muteWindows: [{ dayOfWeek: 1, startMinute: 0, endMinute: 60 }],
  offlineAfterMinutes: 30,
};

const OFFLINE = {
  ...THRESHOLD,
  ruleId: 'r2',
  channelKey: null,
  ruleType: 'offline',
  thresholdLow: null,
  thresholdHigh: null,
  offlineAfterMinutes: 30,
  muteWindows: [],
};

function setup(
  session = SESSION,
  query: Record<string, string> = { assetId: 'a1', templateId: 'tpl-1' },
): {
  fixture: ComponentFixture<RulesPage>;
  http: HttpTestingController;
  page: RulesPage;
} {
  TestBed.configureTestingModule({
    imports: [RulesPage],
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      ...provideTestTransloco(),
      {
        provide: ActivatedRoute,
        useValue: {
          snapshot: {
            paramMap: convertToParamMap({ deviceId: 'd1' }),
            queryParamMap: convertToParamMap(query),
          },
        },
      },
    ],
  });

  TestBed.inject(AuthService).apply(session);
  const http = TestBed.inject(HttpTestingController);
  const fixture = TestBed.createComponent(RulesPage);

  return { fixture, http, page: fixture.componentInstance };
}

describe('RulesPage', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('opens the create form on the plan/06 §8 defaults and sends no quiet windows', async () => {
    const { fixture, http, page } = setup();
    fixture.detectChanges();
    http.expectOne('/api/v1/templates').flush([TEMPLATE]);
    http.expectOne('/api/v1/devices/d1/rules').flush([OFFLINE]);
    await fixture.whenStable();

    page.openCreate();
    expect(page.createForm.getRawValue()).toMatchObject({
      durationSeconds: 60,
      cooldownMinutes: 60,
      severity: 'warn',
      channelKey: 'temp',
    });

    page.createForm.patchValue({ thresholdLow: 2, thresholdHigh: 8 });
    page.create();
    const create = http.expectOne('/api/v1/devices/d1/rules');
    expect(create.request.body).toEqual({
      channelKey: 'temp',
      ruleType: 'outside_range',
      thresholdLow: 2,
      thresholdHigh: 8,
      durationSeconds: 60,
      cooldownMinutes: 60,
      severity: 'warn',
    });
    expect(create.request.body).not.toHaveProperty('muteWindows');

    create.flush(THRESHOLD);
    http.expectOne('/api/v1/devices/d1/rules').flush([THRESHOLD, OFFLINE]);
    await fixture.whenStable();
    expect(page.thresholds().length).toBe(1);
  });

  it('offers only numeric channels that carry no range rule — a bool channel can never fire', async () => {
    const template = {
      ...TEMPLATE,
      channels: [
        ...TEMPLATE.channels,
        { key: 'door', nameAr: 'الباب', nameEn: 'Door', unit: 'state', dataType: 'bool', lowerLimit: null, upperLimit: null },
      ],
    };
    const { fixture, http, page } = setup();
    fixture.detectChanges();
    http.expectOne('/api/v1/templates').flush([template]);
    http.expectOne('/api/v1/devices/d1/rules').flush([OFFLINE]);
    await fixture.whenStable();

    expect(page.openChannels().map((channel) => channel.key)).toEqual(['temp']);

    page.openCreate();
    fixture.detectChanges();
    expect(page.channelOptions().map((option) => option.value)).toEqual(['temp']);

    const dialog = (fixture.nativeElement as HTMLElement).querySelector('[role="dialog"]') as HTMLElement;
    const channel = dialog.querySelector('app-select .ui-select-trigger') as HTMLButtonElement;
    channel.click();
    fixture.detectChanges();
    const rendered = [...document.querySelectorAll<HTMLElement>('.ui-select-option')].map(
      (option) => option.textContent?.trim(),
    );
    // the bool channel is absent from the list itself, not merely filtered after
    expect(rendered).toEqual(['Temperature · °C']);
  });

  it('says why nothing can be created when every numeric channel already has a range rule', async () => {
    const { fixture, http, page } = setup();
    fixture.detectChanges();
    http.expectOne('/api/v1/templates').flush([TEMPLATE]);
    http.expectOne('/api/v1/devices/d1/rules').flush([THRESHOLD, OFFLINE]);
    await fixture.whenStable();

    expect(page.openChannels()).toEqual([]);

    page.openCreate();
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    const dialog = host.querySelector('[role="dialog"]') as HTMLElement;
    expect(dialog).not.toBeNull();
    expect(dialog.textContent).toContain('Every numeric channel already has a range rule');
    const submit = [...dialog.querySelectorAll('button')].find((button) =>
      button.textContent?.includes('Create rule'),
    );
    expect(submit?.disabled).toBe(true);
  });

  it('says why create is unavailable, and does not offer a button that opens a dead picker', async () => {
    const { fixture, http } = setup();
    fixture.detectChanges();
    http.expectOne('/api/v1/templates').flush([TEMPLATE]);
    http.expectOne('/api/v1/devices/d1/rules').flush([THRESHOLD, OFFLINE]);
    await fixture.whenStable();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.textContent).toContain('no numeric channel without a range rule');
    const add = [...host.querySelectorAll('button')].find(
      (button) => button.textContent?.trim() === 'Add threshold rule',
    ) as HTMLButtonElement;
    expect(add.disabled).toBe(true);
  });

  it('refreshes quietly after a write — the table never swaps for the loading line', async () => {
    const { fixture, http, page } = setup();
    fixture.detectChanges();
    http.expectOne('/api/v1/templates').flush([TEMPLATE]);
    http.expectOne('/api/v1/devices/d1/rules').flush([THRESHOLD, OFFLINE]);
    await fixture.whenStable();
    fixture.detectChanges();

    page.deleteTarget.set(THRESHOLD);
    page.remove(THRESHOLD);
    http.expectOne('/api/v1/devices/d1/rules/r1').flush(null, { status: 204, statusText: 'No Content' });
    const refresh = http.expectOne('/api/v1/devices/d1/rules');

    fixture.detectChanges();
    expect(page.loading()).toBe(false);
    expect(page.thresholds().length).toBe(1);
    expect((fixture.nativeElement as HTMLElement).querySelector('table')).not.toBeNull();

    refresh.flush([OFFLINE]);
    await fixture.whenStable();
  });

  it('names the rule by its channel’s template name and unit, never the raw key', async () => {
    const { fixture, http } = setup();
    fixture.detectChanges();
    http.expectOne('/api/v1/templates').flush([TEMPLATE]);
    http.expectOne('/api/v1/devices/d1/rules').flush([THRESHOLD, OFFLINE]);
    await fixture.whenStable();
    fixture.detectChanges();

    const row = (fixture.nativeElement as HTMLElement).querySelector('tbody tr') as HTMLElement;
    expect(row.textContent).toContain('Temperature');
    expect(row.textContent).toContain('2 – 8 °C');
    expect(row.textContent).toContain('60 s · 60 min');
  });

  it('never guesses a template: a direct link without one says so and offers no create', async () => {
    const { fixture, http } = setup(SESSION, {});
    fixture.detectChanges();
    http.expectOne('/api/v1/devices/d1/rules').flush([OFFLINE]);
    await fixture.whenStable();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.textContent).toContain('arrived without its device');
    const add = [...host.querySelectorAll('button')].find(
      (button) => button.textContent?.trim() === 'Add threshold rule',
    ) as HTMLButtonElement;
    expect(add.disabled).toBe(true);
  });

  it('round-trips quiet hours in UTC from the edit view only', async () => {
    const { fixture, http, page } = setup();
    fixture.detectChanges();
    http.expectOne('/api/v1/templates').flush([TEMPLATE]);
    http.expectOne('/api/v1/devices/d1/rules').flush([THRESHOLD, OFFLINE]);
    await fixture.whenStable();

    page.startEdit(THRESHOLD);
    fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Quiet hours (UTC)');

    page.addWindow();
    page.setDay(1, '2');
    const start = document.createElement('input');
    start.value = '00:00';
    page.setTime(1, 'start', { target: start } as unknown as Event);
    page.saveEdit(THRESHOLD);

    const patch = http.expectOne('/api/v1/devices/d1/rules/r1');
    expect(patch.request.method).toBe('PATCH');
    expect(patch.request.body.muteWindows).toEqual([
      { dayOfWeek: 1, startMinute: 0, endMinute: 60 },
      { dayOfWeek: 2, startMinute: 0, endMinute: 60 },
    ]);

    patch.flush(THRESHOLD);
    http.expectOne('/api/v1/devices/d1/rules').flush([THRESHOLD, OFFLINE]);
    await fixture.whenStable();
  });

  it('shows each quiet window on its OWN day in the drawn select', async () => {
    const { fixture, http, page } = setup();
    fixture.detectChanges();
    http.expectOne('/api/v1/templates').flush([TEMPLATE]);
    http.expectOne('/api/v1/devices/d1/rules').flush([THRESHOLD, OFFLINE]);
    await fixture.whenStable();

    page.startEdit({ ...THRESHOLD, muteWindows: [{ dayOfWeek: 2, startMinute: 780, endMinute: 900 }] });
    fixture.detectChanges();

    const triggers = [
      ...(fixture.nativeElement as HTMLElement).querySelectorAll(
        '[role="dialog"] app-select .ui-select-trigger',
      ),
    ];
    // the second select is the day of the window (0 = Sunday), so it reads Tuesday
    // rather than the list's first day
    expect(triggers.map((trigger) => trigger.textContent?.trim())).toContain('Tuesday');
  });

  it('changes a quiet window’s day from the drawn list', async () => {
    const { fixture, http, page } = setup();
    fixture.detectChanges();
    http.expectOne('/api/v1/templates').flush([TEMPLATE]);
    http.expectOne('/api/v1/devices/d1/rules').flush([THRESHOLD, OFFLINE]);
    await fixture.whenStable();

    page.startEdit({ ...THRESHOLD, muteWindows: [{ dayOfWeek: 2, startMinute: 780, endMinute: 900 }] });
    fixture.detectChanges();

    const day = [
      ...(fixture.nativeElement as HTMLElement).querySelectorAll(
        '[role="dialog"] app-select .ui-select-trigger',
      ),
    ][1] as HTMLButtonElement;
    day.click();
    fixture.detectChanges();
    const friday = [...document.querySelectorAll<HTMLElement>('.ui-select-option')].find(
      (option) => option.textContent?.trim() === 'Friday',
    );
    friday!.click();
    fixture.detectChanges();

    expect(page.windows()[0].dayOfWeek).toBe(5);
    expect(day.textContent?.trim()).toBe('Friday');
  });

  it('tunes the seeded offline guard without sending thresholds or windows', async () => {
    const { fixture, http, page } = setup();
    fixture.detectChanges();
    http.expectOne('/api/v1/templates').flush([TEMPLATE]);
    http.expectOne('/api/v1/devices/d1/rules').flush([THRESHOLD, OFFLINE]);
    await fixture.whenStable();

    expect(page.offline()?.ruleId).toBe('r2');

    page.offlineForm.patchValue({ offlineAfterMinutes: 45, severity: 'critical', enabled: true });
    page.saveOffline(OFFLINE);

    const patch = http.expectOne('/api/v1/devices/d1/rules/r2');
    expect(patch.request.body).toEqual({
      offlineAfterMinutes: 45,
      cooldownMinutes: 60,
      severity: 'critical',
      enabled: true,
    });

    patch.flush({ ...OFFLINE, offlineAfterMinutes: 45, severity: 'critical' });
    http.expectOne('/api/v1/devices/d1/rules').flush([THRESHOLD, { ...OFFLINE, offlineAfterMinutes: 45 }]);
    await fixture.whenStable();
  });

  it('maps an overlapping-window 422 to its own localized refusal', async () => {
    const { fixture, http, page } = setup();
    fixture.detectChanges();
    http.expectOne('/api/v1/templates').flush([TEMPLATE]);
    http.expectOne('/api/v1/devices/d1/rules').flush([THRESHOLD, OFFLINE]);

    page.startEdit(THRESHOLD);
    page.saveEdit(THRESHOLD);
    http
      .expectOne('/api/v1/devices/d1/rules/r1')
      .flush({ code: 'rules:mute_window_overlap' }, { status: 422, statusText: 'Unprocessable' });

    expect(page.error()?.key).toBe('errors.muteWindowOverlap');
  });

  it('shows the seeded offline guard once, with the §8 default, and no delete affordance', async () => {
    const { fixture, http, page } = setup();
    fixture.detectChanges();
    http.expectOne('/api/v1/templates').flush([TEMPLATE]);
    http.expectOne('/api/v1/devices/d1/rules').flush([OFFLINE]);
    await fixture.whenStable();
    fixture.detectChanges();

    expect(page.rules().filter((rule) => rule.ruleType === 'offline').length).toBe(1);
    expect(page.thresholds().length).toBe(0);
    expect(page.offlineForm.getRawValue().offlineAfterMinutes).toBe(30);

    const host = fixture.nativeElement as HTMLElement;
    expect(host.textContent).toContain('Offline guard');
    const labels = [...host.querySelectorAll('button')].map((button) => button.textContent?.trim());
    expect(labels).not.toContain('Delete');
    expect(labels).not.toContain('Add offline rule');
  });

  it('keeps a viewer read-only: no add, no edit/delete, and the guard is disabled', async () => {
    const viewer = { ...SESSION, user: { ...SESSION.user, role: 'viewer' } };
    const { fixture, http } = setup(viewer);
    fixture.detectChanges();
    http.expectOne('/api/v1/templates').flush([TEMPLATE]);
    http.expectOne('/api/v1/devices/d1/rules').flush([THRESHOLD, OFFLINE]);
    await fixture.whenStable();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.textContent).not.toContain('Add threshold rule');

    const labels = [...host.querySelectorAll('button')].map((button) => button.textContent?.trim());
    expect(labels).not.toContain('Edit');
    expect(labels).not.toContain('Delete');
    expect(labels).not.toContain('Save guard');
    expect(host.querySelector('fieldset')?.disabled).toBe(true);
    const severity = host.querySelector(
      'fieldset app-select .ui-select-trigger',
    ) as HTMLButtonElement;
    expect(severity.disabled).toBe(true);
  });
});
