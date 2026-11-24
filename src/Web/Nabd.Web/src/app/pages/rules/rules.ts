import { NgTemplateOutlet } from '@angular/common';
import { Component, computed, ElementRef, inject, OnInit, signal, viewChild } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { ApiProblem, describeApiError } from '../../core/api/api-error';
import { MuteWindow, RuleRow, TenantAdminApi } from '../../core/api/tenant-admin.service';
import { AuthService } from '../../core/auth/auth.service';
import { NoticeService } from '../../core/ui/notice.service';
import { SelectComponent, type SelectOption } from '../../core/ui/select';

const DAYS = [0, 1, 2, 3, 4, 5, 6] as const;

function minutesToTime(minutes: number): string {
  const h = Math.floor(minutes / 60)
    .toString()
    .padStart(2, '0');
  const m = (minutes % 60).toString().padStart(2, '0');
  return `${h}:${m}`;
}

function timeToMinutes(value: string): number {
  const [h, m] = value.split(':').map(Number);
  return (h || 0) * 60 + (m || 0);
}

/**
 * Rules (spec 02): threshold rules are created here; quiet hours ride the
 * EDIT view only (the create body has no such leg), in UTC with a local
 * preview; the offline rule is seeded with the device and is only tuned —
 * no create, no delete.
 */
@Component({
  selector: 'app-rules-page',
  imports: [NgTemplateOutlet, ReactiveFormsModule, RouterLink, SelectComponent, TranslocoPipe],
  template: `
    <section class="ui-page">
      <header class="ui-head">
        <div>
          <span class="ui-kicker">
            <a [routerLink]="['/assets', assetId(), 'devices']">{{ 'rules.backToDevice' | transloco }}</a>
          </span>
          <h1 class="ui-title">{{ 'rules.title' | transloco }}</h1>
          <p class="ui-sub">{{ 'rules.body' | transloco }}</p>
        </div>
        @if (canManage()) {
          <button
            class="ui-btn is-primary"
            type="button"
            (click)="openCreate()"
            [disabled]="!openChannels().length"
            [attr.title]="openChannels().length ? null : ('rules.noFreeChannel' | transloco)"
          >
            {{ 'rules.add' | transloco }}
          </button>
        }
      </header>

      <ng-template #problem>
        @if (error(); as problem) {
          <p class="ui-note is-error">
            {{ problem.key | transloco }}
            @if (problem.code) {
              <code>{{ problem.code }}</code>
            }
          </p>
        }
      </ng-template>

      @if (!creating() && !editing() && !deleteTarget()) {
        <ng-container *ngTemplateOutlet="problem" />
      }

      <!-- the truth before the table: why "add" is unavailable, and what each
           kind of rule means -->
      @if (!loading() && templatesReady() && !openChannels().length) {
        <p class="ui-note">
          {{
            (!templateId
              ? 'rules.noTemplateContext'
              : hasNumericChannels()
                ? 'rules.noFreeChannelBody'
                : 'rules.noNumericChannel'
            ) | transloco
          }}
        </p>
      }

      @if (creating()) {
        <div class="ui-overlay" role="presentation" (keydown.escape)="closeCreate()">
          <div class="ui-dialog" role="dialog" aria-modal="true" aria-labelledby="create-rule">
            <h2 id="create-rule">{{ 'rules.createTitle' | transloco }}</h2>
            <ng-container *ngTemplateOutlet="problem" />
            <form [formGroup]="createForm" (ngSubmit)="create()">
              <div class="ui-grid">
                @if (openChannels().length) {
                  <div class="ui-field">
                    <span>{{ 'rules.channel' | transloco }}</span>
                    <app-select
                      formControlName="channelKey"
                      [options]="channelOptions()"
                      [ariaLabel]="'rules.channel' | transloco"
                    />
                  </div>
                }
                <label class="ui-field">
                  <span>{{ 'rules.low' | transloco }} ({{ unitFor(createForm.getRawValue().channelKey) }})</span>
                  <input #createLow type="number" step="any" formControlName="thresholdLow" />
                </label>
                <label class="ui-field">
                  <span>{{ 'rules.high' | transloco }} ({{ unitFor(createForm.getRawValue().channelKey) }})</span>
                  <input type="number" step="any" formControlName="thresholdHigh" />
                </label>
                <label class="ui-field">
                  <span>{{ 'rules.duration' | transloco }}</span>
                  <input type="number" min="1" formControlName="durationSeconds" />
                </label>
                <label class="ui-field">
                  <span>{{ 'rules.cooldown' | transloco }}</span>
                  <input type="number" min="0" formControlName="cooldownMinutes" />
                </label>
                <div class="ui-field">
                  <span>{{ 'rules.severity' | transloco }}</span>
                  <app-select
                    formControlName="severity"
                    [options]="severityOptions"
                    [ariaLabel]="'rules.severity' | transloco"
                  />
                </div>
              </div>
              @if (openChannels().length === 0) {
                <p class="ui-note">{{ 'rules.noFreeChannel' | transloco }}</p>
              }
              <p class="ui-hint">{{ 'rules.quietLater' | transloco }}</p>
              <div class="ui-actions">
                <button class="ui-btn" type="button" (click)="closeCreate()" [disabled]="busy()">
                  {{ 'common.cancel' | transloco }}
                </button>
                <button
                  class="ui-btn is-primary"
                  type="submit"
                  [disabled]="createForm.invalid || busy() || openChannels().length === 0"
                >
                  {{ (busy() ? 'rules.creating' : 'rules.create') | transloco }}
                </button>
              </div>
            </form>
          </div>
        </div>
      }

      @if (editing(); as rule) {
        <div class="ui-overlay" role="presentation" (keydown.escape)="editing.set(null)">
          <div class="ui-dialog" role="dialog" aria-modal="true" aria-labelledby="edit-rule">
            <h2 id="edit-rule">{{ 'rules.editTitle' | transloco }}</h2>
            <ng-container *ngTemplateOutlet="problem" />
            <form [formGroup]="editForm" (ngSubmit)="saveEdit(rule)">
              <div class="ui-grid">
                <label class="ui-field">
                  <span>{{ 'rules.channel' | transloco }}</span>
                  <input type="text" formControlName="channelKey" />
                </label>
                <label class="ui-field">
                  <span>{{ 'rules.low' | transloco }} ({{ unitFor(rule.channelKey) }})</span>
                  <input type="number" step="any" formControlName="thresholdLow" />
                </label>
                <label class="ui-field">
                  <span>{{ 'rules.high' | transloco }} ({{ unitFor(rule.channelKey) }})</span>
                  <input type="number" step="any" formControlName="thresholdHigh" />
                </label>
                <label class="ui-field">
                  <span>{{ 'rules.duration' | transloco }}</span>
                  <input type="number" min="1" formControlName="durationSeconds" />
                </label>
                <label class="ui-field">
                  <span>{{ 'rules.cooldown' | transloco }}</span>
                  <input type="number" min="0" formControlName="cooldownMinutes" />
                </label>
                <div class="ui-field">
                  <span>{{ 'rules.severity' | transloco }}</span>
                  <app-select
                    formControlName="severity"
                    [options]="severityOptions"
                    [ariaLabel]="'rules.severity' | transloco"
                  />
                </div>
                <label class="ui-check">
                  <input type="checkbox" formControlName="enabled" />
                  <span class="ui-check-box" aria-hidden="true"></span>
                  {{ 'rules.enabled' | transloco }}
                </label>
              </div>

              <h3 class="ui-title">{{ 'rules.quietTitle' | transloco }}</h3>
              <p class="ui-hint">{{ 'rules.quietHint' | transloco }}</p>

              @if (!windows().length) {
                <p class="ui-hint">{{ 'rules.quietNone' | transloco }}</p>
              } @else {
                @for (window of windows(); track $index) {
                  <div class="ui-grid">
                    <div class="ui-field">
                      <span>{{ 'rules.day' | transloco }}</span>
                      <app-select
                        [options]="dayOptions"
                        [value]="window.dayOfWeek + ''"
                        [ariaLabel]="'rules.day' | transloco"
                        (valueChange)="setDay($index, $event)"
                      />
                    </div>
                    <label class="ui-field">
                      <span>{{ 'rules.start' | transloco }} ({{ 'rules.utcNote' | transloco }})</span>
                      <input type="time" [value]="timeOf(window.startMinute)" (change)="setTime($index, 'start', $event)" />
                    </label>
                    <label class="ui-field">
                      <span>{{ 'rules.end' | transloco }} ({{ 'rules.utcNote' | transloco }})</span>
                      <input type="time" [value]="timeOf(window.endMinute)" (change)="setTime($index, 'end', $event)" />
                    </label>
                    <p class="ui-hint">{{ 'rules.preview' | transloco }}: {{ localPreview(window) }}</p>
                  </div>
                  <button class="ui-btn is-small is-danger" type="button" (click)="removeWindow($index)">
                    {{ 'rules.quietRemove' | transloco }}
                  </button>
                }
              }

              <div class="ui-actions">
                <button class="ui-btn" type="button" (click)="addWindow()">
                  {{ 'rules.quietAdd' | transloco }}
                </button>
                <button class="ui-btn" type="button" (click)="editing.set(null)" [disabled]="busy()">
                  {{ 'common.cancel' | transloco }}
                </button>
                <button class="ui-btn is-primary" type="submit" [disabled]="editForm.invalid || busy()">
                  {{ (busy() ? 'rules.saving' : 'rules.save') | transloco }}
                </button>
              </div>
            </form>
          </div>
        </div>
      }

      @if (deleteTarget(); as rule) {
        <div class="ui-overlay" role="presentation">
          <div class="ui-dialog" role="dialog" aria-modal="true" aria-labelledby="delete-rule">
            <h2 id="delete-rule">{{ 'rules.deleteTitle' | transloco }}</h2>
            <p class="ui-note">{{ 'rules.deleteBody' | transloco }}</p>
            <ng-container *ngTemplateOutlet="problem" />
            <div class="ui-actions">
              <button class="ui-btn" type="button" (click)="deleteTarget.set(null)">
                {{ 'common.cancel' | transloco }}
              </button>
              <button class="ui-btn is-danger" type="button" (click)="remove(rule)" [disabled]="busy()">
                {{ 'common.delete' | transloco }}
              </button>
            </div>
          </div>
        </div>
      }

      @if (loading()) {
        <p class="ui-note">{{ 'common.loading' | transloco }}</p>
      } @else {
        <h2 class="ui-title">{{ 'rules.thresholdTitle' | transloco }}</h2>
        <p class="ui-hint">{{ 'rules.thresholdHint' | transloco }}</p>
        @if (!thresholds().length) {
          <p class="ui-panel ui-empty">{{ 'rules.empty' | transloco }}</p>
        } @else {
          <div class="ui-table-wrap">
            <table class="ui-table">
              <thead>
                <tr>
                  <th scope="col">{{ 'rules.channel' | transloco }}</th>
                  <th scope="col">{{ 'rules.low' | transloco }} / {{ 'rules.high' | transloco }}</th>
                  <th scope="col">{{ 'rules.duration' | transloco }}</th>
                  <th scope="col">{{ 'rules.severity' | transloco }}</th>
                  @if (canManage()) {
                    <th scope="col">{{ 'common.actions' | transloco }}</th>
                  }
                </tr>
              </thead>
              <tbody>
                @for (rule of thresholds(); track rule.ruleId) {
                  <tr>
                    <td>{{ channelLabel(rule.channelKey) }}</td>
                    <td>
                      {{ rule.thresholdLow ?? '—' }} – {{ rule.thresholdHigh ?? '—' }}
                      {{ unitFor(rule.channelKey) }}
                    </td>
                    <td>
                      {{ rule.durationSeconds }} {{ 'rules.secondsShort' | transloco }} ·
                      {{ rule.cooldownMinutes }} {{ 'rules.minutesShort' | transloco }}
                    </td>
                    <td>
                      <span class="ui-badge" [class.is-warn]="rule.severity === 'warn'" [class.is-critical]="rule.severity === 'critical'">
                        {{ (rule.severity === 'critical' ? 'rules.severityCritical' : 'rules.severityWarn') | transloco }}
                      </span>
                    </td>
                    @if (canManage()) {
                      <td>
                        <div class="ui-actions">
                          <button class="ui-btn is-small" type="button" (click)="startEdit(rule)">
                            {{ 'common.edit' | transloco }}
                          </button>
                          <button class="ui-btn is-small is-danger" type="button" (click)="deleteTarget.set(rule)">
                            {{ 'common.delete' | transloco }}
                          </button>
                        </div>
                      </td>
                    }
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
        <br>
        @if (offline(); as rule) {
          <form class="ui-panel" [formGroup]="offlineForm" (ngSubmit)="saveOffline(rule)">
            <h2 class="ui-title">{{ 'rules.offlineTitle' | transloco }}</h2>
            <p class="ui-hint">{{ 'rules.offlineBody' | transloco }}</p>
            <fieldset class="ui-grid ui-fieldset" [disabled]="!canManage()">
              <label class="ui-field">
                <span>{{ 'rules.offlineAfter' | transloco }}</span>
                <input type="number" min="1" formControlName="offlineAfterMinutes" />
              </label>
              <label class="ui-field">
                <span>{{ 'rules.cooldown' | transloco }}</span>
                <input type="number" min="0" formControlName="cooldownMinutes" />
              </label>
              <div class="ui-field">
                <span>{{ 'rules.severity' | transloco }}</span>
                <app-select
                  formControlName="severity"
                  [options]="severityOptions"
                  [disabled]="!canManage()"
                  [ariaLabel]="'rules.severity' | transloco"
                />
              </div>
              <label class="ui-check">
                <input type="checkbox" formControlName="enabled" />
                <span class="ui-check-box" aria-hidden="true"></span>
                {{ 'rules.enabled' | transloco }}
              </label>
            </fieldset>
            @if (canManage()) {
              <div class="ui-actions">
                <button class="ui-btn is-primary" type="submit" [disabled]="offlineForm.invalid || busy()">
                  {{ (busy() ? 'rules.offlineSaving' : 'rules.offlineSave') | transloco }}
                </button>
              </div>
            }
          </form>
        }
      }
    </section>
  `,
})
export class RulesPage implements OnInit {
  private readonly api = inject(TenantAdminApi);
  private readonly auth = inject(AuthService);
  private readonly notice = inject(NoticeService);
  private readonly route = inject(ActivatedRoute);
  private readonly fb = inject(FormBuilder);
  private readonly lowInput = viewChild<ElementRef<HTMLInputElement>>('createLow');
  private readonly transloco = inject(TranslocoService);

  readonly days = DAYS;
  readonly severityOptions: readonly SelectOption[] = [
    { value: 'warn', labelKey: 'rules.severityWarn' },
    { value: 'critical', labelKey: 'rules.severityCritical' },
  ];
  readonly dayOptions: readonly SelectOption[] = DAYS.map((day) => ({
    value: String(day),
    labelKey: `rules.day${day}`,
  }));
  readonly deviceId = this.route.snapshot.paramMap.get('deviceId') ?? '';
  readonly assetId = signal(this.route.snapshot.queryParamMap.get('assetId') ?? '');
  readonly templateId = this.route.snapshot.queryParamMap.get('templateId') ?? '';
  readonly channels = signal<
    { key: string; nameAr: string; nameEn: string; unit: string; dataType: string }[]
  >([]);

  readonly rules = signal<RuleRow[]>([]);
  readonly loading = signal(true);
  readonly error = signal<ApiProblem | null>(null);
  readonly creating = signal(false);
  readonly busy = signal(false);
  readonly editing = signal<RuleRow | null>(null);
  readonly deleteTarget = signal<RuleRow | null>(null);
  readonly windows = signal<MuteWindow[]>([]);

  readonly canManage = computed(() => this.auth.user()?.role !== 'viewer');
  readonly thresholds = computed(() => this.rules().filter((rule) => rule.ruleType !== 'offline'));
  readonly offline = computed(() => this.rules().find((rule) => rule.ruleType === 'offline'));

  /**
   * The create form can only offer a channel the server will actually accept:
   * numeric (a boolean channel never carries a range — the door would take it and
   * the rule could never fire) and not already guarded by an `outside_range`
   * rule (the template presets seed those at mint — `0021`'s UNIQUE answers a
   * second one with `rules:duplicate_rule`). A picker that invites a guaranteed
   * 422 is a fake affordance; an empty list says why instead.
   */
  readonly openChannels = computed(() => {
    const taken = new Set(
      this.rules()
        .filter((rule) => rule.ruleType === 'outside_range')
        .map((rule) => rule.channelKey),
    );
    return this.channels().filter(
      (channel) => channel.dataType === 'number' && !taken.has(channel.key),
    );
  });

  readonly hasNumericChannels = computed(() =>
    this.channels().some((channel) => channel.dataType === 'number'),
  );

  readonly channelOptions = computed<readonly SelectOption[]>(() => {
    const arabic = this.transloco.activeLang() === 'ar';
    return this.openChannels().map((channel) => ({
      value: channel.key,
      label: `${arabic ? channel.nameAr : channel.nameEn} · ${channel.unit}`,
    }));
  });

  /** False until the template answer lands (or is not needed): no note on a guess. */
  readonly templatesReady = signal(!this.templateId);

  readonly createForm = this.fb.group({
    channelKey: ['', Validators.required],
    thresholdLow: [null as number | null],
    thresholdHigh: [null as number | null],
    durationSeconds: [60, [Validators.required, Validators.min(1)]],
    cooldownMinutes: [60, [Validators.required, Validators.min(0)]],
    severity: ['warn'],
  });

  readonly editForm = this.fb.group({
    channelKey: [{ value: '', disabled: true }],
    thresholdLow: [null as number | null],
    thresholdHigh: [null as number | null],
    durationSeconds: [60, [Validators.required, Validators.min(1)]],
    cooldownMinutes: [60, [Validators.required, Validators.min(0)]],
    severity: ['warn'],
    enabled: [true],
  });

  readonly offlineForm = this.fb.group({
    offlineAfterMinutes: [30, [Validators.required, Validators.min(1)]],
    cooldownMinutes: [60, [Validators.required, Validators.min(0)]],
    severity: ['warn'],
    enabled: [true],
  });

  ngOnInit(): void {
    if (this.templateId) {
      this.api.templates().subscribe({
        next: (templates) => {
          const template = templates.find((item) => item.templateId === this.templateId);
          this.channels.set(template?.channels ?? []);
          this.templatesReady.set(true);
        },
        error: (error: unknown) => {
          this.error.set(describeApiError(error));
          this.templatesReady.set(true);
        },
      });
    }
    this.load();
  }

  /** `quiet` refreshes in place — a save must not blink the table it confirms. */
  load(quiet = false): void {
    if (!quiet) this.loading.set(true);
    this.api.rules(this.deviceId).subscribe({
      next: (rules) => {
        this.rules.set(rules);
        this.loading.set(false);
        this.repickChannel();
        const guard = rules.find((rule) => rule.ruleType === 'offline');
        if (guard) {
          this.offlineForm.patchValue({
            offlineAfterMinutes: guard.offlineAfterMinutes ?? 30,
            cooldownMinutes: guard.cooldownMinutes,
            severity: guard.severity,
            enabled: guard.enabled,
          });
        }
      },
      error: (error: unknown) => {
        this.error.set(describeApiError(error));
        if (!quiet) this.rules.set([]);
        this.loading.set(false);
      },
    });
  }

  openCreate(): void {
    this.error.set(null);
    this.createForm.reset({
      channelKey: this.openChannels()[0]?.key ?? '',
      thresholdLow: null,
      thresholdHigh: null,
      durationSeconds: 60,
      cooldownMinutes: 60,
      severity: 'warn',
    });
    this.creating.set(true);
    setTimeout(() => this.lowInput()?.nativeElement.focus());
  }

  closeCreate(): void {
    if (this.busy()) return;
    this.creating.set(false);
    this.error.set(null);
  }

  private repickChannel(): void {
    const current = this.createForm.getRawValue().channelKey;
    if (this.openChannels().some((channel) => channel.key === current)) return;
    this.createForm.patchValue({ channelKey: this.openChannels()[0]?.key ?? '' });
  }

  create(): void {
    if (this.createForm.invalid || this.busy()) return;
    const value = this.createForm.getRawValue();
    if (!this.openChannels().some((channel) => channel.key === value.channelKey)) {
      // nothing lawful to create — the form says why (openChannels empty) or the
      // picker was stale; never fire a request the door will refuse
      this.error.set({ code: null, key: 'errors.invalidRule' });
      return;
    }
    if (value.thresholdLow == null && value.thresholdHigh == null) {
      this.error.set({ code: null, key: 'errors.invalidRule' });
      return;
    }
    this.busy.set(true);
    this.error.set(null);

    this.api
      .createRule(this.deviceId, {
        channelKey: value.channelKey,
        // the door's law (DeviceRules.ThresholdsValid): low + high = outside_range;
        // "threshold" was never a rule type and every create answered 422
        ruleType: 'outside_range',
        thresholdLow: value.thresholdLow,
        thresholdHigh: value.thresholdHigh,
        durationSeconds: value.durationSeconds ?? 60,
        cooldownMinutes: value.cooldownMinutes ?? 60,
        severity: value.severity ?? 'warn',
      })
      .subscribe({
        next: () => {
          this.busy.set(false);
          this.creating.set(false);
          this.notice.show('toast.ruleCreated', 'success');
          this.load(true);
        },
        error: (error: unknown) => {
          this.busy.set(false);
          this.error.set(describeApiError(error));
          this.notice.show(describeApiError(error).key, 'error');
        },
      });
  }

  startEdit(rule: RuleRow): void {
    this.editing.set(rule);
    this.windows.set((rule.muteWindows ?? []).map((window) => ({ ...window })));
    this.editForm.reset({
      channelKey: rule.channelKey ?? '',
      thresholdLow: rule.thresholdLow,
      thresholdHigh: rule.thresholdHigh,
      durationSeconds: rule.durationSeconds,
      cooldownMinutes: rule.cooldownMinutes,
      severity: rule.severity,
      enabled: rule.enabled,
    });
    this.error.set(null);
  }

  saveEdit(rule: RuleRow): void {
    if (this.editForm.invalid || this.busy()) return;
    const value = this.editForm.getRawValue();
    this.busy.set(true);
    this.error.set(null);

    this.api
      .updateRule(this.deviceId, rule.ruleId, {
        thresholdLow: value.thresholdLow,
        thresholdHigh: value.thresholdHigh,
        durationSeconds: value.durationSeconds ?? 60,
        cooldownMinutes: value.cooldownMinutes ?? 60,
        severity: value.severity ?? 'warn',
        enabled: value.enabled ?? true,
        muteWindows: this.windows(),
      })
      .subscribe({
        next: () => {
          this.busy.set(false);
          this.editing.set(null);
          this.notice.show('toast.ruleUpdated', 'success');
          this.load(true);
        },
        error: (error: unknown) => {
          this.busy.set(false);
          this.error.set(describeApiError(error));
          this.notice.show(describeApiError(error).key, 'error');
        },
      });
  }

  saveOffline(rule: RuleRow): void {
    if (this.offlineForm.invalid || this.busy()) return;
    const value = this.offlineForm.getRawValue();
    this.busy.set(true);
    this.error.set(null);

    this.api
      .updateRule(this.deviceId, rule.ruleId, {
        offlineAfterMinutes: value.offlineAfterMinutes ?? 30,
        cooldownMinutes: value.cooldownMinutes ?? 60,
        severity: value.severity ?? 'warn',
        enabled: value.enabled ?? true,
      })
      .subscribe({
        next: () => {
          this.busy.set(false);
          this.notice.show('toast.guardSaved', 'success');
          this.load(true);
        },
        error: (error: unknown) => {
          this.busy.set(false);
          this.error.set(describeApiError(error));
          this.notice.show(describeApiError(error).key, 'error');
        },
      });
  }

  remove(rule: RuleRow): void {
    this.busy.set(true);
    this.error.set(null);
    this.api.deleteRule(this.deviceId, rule.ruleId).subscribe({
      next: () => {
        this.busy.set(false);
        this.deleteTarget.set(null);
        this.notice.show('toast.ruleDeleted', 'success');
        this.load(true);
      },
      error: (error: unknown) => {
        this.busy.set(false);
        this.error.set(describeApiError(error));
        this.notice.show(describeApiError(error).key, 'error');
        this.load(true);
      },
    });
  }

  addWindow(): void {
    if (this.windows().length >= 20) return;
    this.windows.update((windows) => [...windows, { dayOfWeek: 1, startMinute: 0, endMinute: 60 }]);
  }

  removeWindow(index: number): void {
    this.windows.update((windows) => windows.filter((_, i) => i !== index));
  }

  setDay(index: number, value: string): void {
    this.windows.update((windows) =>
      windows.map((window, i) => (i === index ? { ...window, dayOfWeek: Number(value) } : window)),
    );
  }

  setTime(index: number, leg: 'start' | 'end', event: Event): void {
    const minutes = timeToMinutes((event.target as HTMLInputElement).value);
    this.windows.update((windows) =>
      windows.map((window, i) =>
        i === index
          ? leg === 'start'
            ? { ...window, startMinute: minutes }
            : { ...window, endMinute: minutes }
          : window,
      ),
    );
  }

  timeOf(minutes: number): string {
    return minutesToTime(minutes);
  }

  /** The truth is UTC; this line is the honest local equivalent. */
  localPreview(window: MuteWindow): string {
    const now = new Date();
    const startOfWeek = Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate());
    const dayOffset = (window.dayOfWeek - now.getUTCDay() + 7) % 7;
    const at = new Date(startOfWeek + dayOffset * 86_400_000 + window.startMinute * 60_000);
    const locale = this.transloco.getActiveLang() === 'ar' ? 'ar' : 'en';
    const day = new Intl.DateTimeFormat(locale, { weekday: 'long' }).format(at);
    const time = new Intl.DateTimeFormat(locale, { hour: '2-digit', minute: '2-digit' }).format(at);
    const end = new Intl.DateTimeFormat(locale, { hour: '2-digit', minute: '2-digit' }).format(
      new Date(at.getTime() + (window.endMinute - window.startMinute) * 60_000),
    );
    return `${day} ${time}–${end}`;
  }

  channelName(channel: { nameAr: string; nameEn: string }): string {
    return this.isArabic() ? channel.nameAr : channel.nameEn;
  }

  /** The table must speak the template's language, not the channel's key. */
  channelLabel(channelKey: string | null): string {
    const channel = this.channels().find((item) => item.key === channelKey);
    return channel ? this.channelName(channel) : (channelKey ?? '—');
  }

  unitFor(channelKey: string | null): string {
    return this.channels().find((channel) => channel.key === channelKey)?.unit ?? '';
  }

  private isArabic(): boolean {
    return this.transloco.activeLang() === 'ar';
  }
}
