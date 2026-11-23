import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { ApiProblem, describeApiError } from '../../core/api/api-error';
import { MuteWindow, RuleRow, TenantAdminApi } from '../../core/api/tenant-admin.service';
import { AuthService } from '../../core/auth/auth.service';

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
  imports: [ReactiveFormsModule, RouterLink, TranslocoPipe],
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
          <button class="ui-btn is-primary" type="button" (click)="toggleCreate()">
            {{ (creating() ? 'common.cancel' : 'rules.add') | transloco }}
          </button>
        }
      </header>

      @if (error(); as problem) {
        <p class="ui-note is-error">
          {{ problem.key | transloco }}
          @if (problem.code) {
            <code>{{ problem.code }}</code>
          }
        </p>
      }

      @if (creating()) {
        <form class="ui-panel" [formGroup]="createForm" (ngSubmit)="create()">
          <h2 class="ui-title">{{ 'rules.createTitle' | transloco }}</h2>
          <div class="ui-grid">
            <label class="ui-field">
              <span>{{ 'rules.channel' | transloco }}</span>
              <select formControlName="channelKey">
                @for (channel of channels(); track channel.key) {
                  <option [value]="channel.key">{{ channelName(channel) }} · {{ channel.unit }}</option>
                }
              </select>
            </label>
            <label class="ui-field">
              <span>{{ 'rules.low' | transloco }} ({{ unitFor(createForm.getRawValue().channelKey) }})</span>
              <input type="number" step="any" formControlName="thresholdLow" />
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
            <label class="ui-field">
              <span>{{ 'rules.severity' | transloco }}</span>
              <select formControlName="severity">
                <option value="warn">{{ 'rules.severityWarn' | transloco }}</option>
                <option value="critical">{{ 'rules.severityCritical' | transloco }}</option>
              </select>
            </label>
          </div>
          <p class="ui-hint">{{ 'rules.quietLater' | transloco }}</p>
          <div class="ui-actions">
            <button class="ui-btn is-primary" type="submit" [disabled]="createForm.invalid || busy()">
              {{ (busy() ? 'rules.creating' : 'rules.create') | transloco }}
            </button>
          </div>
        </form>
      }

      @if (editing(); as rule) {
        <form class="ui-panel" [formGroup]="editForm" (ngSubmit)="saveEdit(rule)">
          <h2 class="ui-title">{{ 'rules.editTitle' | transloco }}</h2>
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
            <label class="ui-field">
              <span>{{ 'rules.severity' | transloco }}</span>
              <select formControlName="severity">
                <option value="warn">{{ 'rules.severityWarn' | transloco }}</option>
                <option value="critical">{{ 'rules.severityCritical' | transloco }}</option>
              </select>
            </label>
            <label class="ui-check">
              <input type="checkbox" formControlName="enabled" />
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
                <label class="ui-field">
                  <span>{{ 'rules.day' | transloco }}</span>
                  <select [value]="window.dayOfWeek" (change)="setDay($index, $any($event.target).value)">
                    @for (day of days; track day) {
                      <option [value]="day">{{ 'rules.day' + day | transloco }}</option>
                    }
                  </select>
                </label>
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
            <button class="ui-btn is-primary" type="submit" [disabled]="editForm.invalid || busy()">
              {{ (busy() ? 'rules.saving' : 'rules.save') | transloco }}
            </button>
            <button class="ui-btn" type="button" (click)="editing.set(null)">
              {{ 'common.cancel' | transloco }}
            </button>
          </div>
        </form>
      }

      @if (deleteTarget(); as rule) {
        <div class="ui-overlay" role="presentation">
          <div class="ui-dialog" role="dialog" aria-modal="true" aria-labelledby="delete-rule">
            <h2 id="delete-rule">{{ 'rules.deleteTitle' | transloco }}</h2>
            <p class="ui-note">{{ 'rules.deleteBody' | transloco }}</p>
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
                    <td>{{ rule.channelKey }}</td>
                    <td>{{ rule.thresholdLow ?? '—' }} / {{ rule.thresholdHigh ?? '—' }}</td>
                    <td>{{ rule.durationSeconds }}s · {{ rule.cooldownMinutes }}m</td>
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
              <label class="ui-field">
                <span>{{ 'rules.severity' | transloco }}</span>
                <select formControlName="severity">
                  <option value="warn">{{ 'rules.severityWarn' | transloco }}</option>
                  <option value="critical">{{ 'rules.severityCritical' | transloco }}</option>
                </select>
              </label>
              <label class="ui-check">
                <input type="checkbox" formControlName="enabled" />
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
  private readonly route = inject(ActivatedRoute);
  private readonly fb = inject(FormBuilder);
  private readonly transloco = inject(TranslocoService);

  readonly days = DAYS;
  readonly deviceId = this.route.snapshot.paramMap.get('deviceId') ?? '';
  readonly assetId = signal(this.route.snapshot.queryParamMap.get('assetId') ?? '');
  readonly templateId = this.route.snapshot.queryParamMap.get('templateId') ?? '';
  readonly channels = signal<{ key: string; nameAr: string; nameEn: string; unit: string }[]>([]);

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
          if (template?.channels[0]) this.createForm.patchValue({ channelKey: template.channels[0].key });
        },
        error: (error: unknown) => this.error.set(describeApiError(error)),
      });
    }
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.api.rules(this.deviceId).subscribe({
      next: (rules) => {
        this.rules.set(rules);
        this.loading.set(false);
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
        this.rules.set([]);
        this.loading.set(false);
      },
    });
  }

  toggleCreate(): void {
    this.creating.update((open) => !open);
    this.error.set(null);
    this.createForm.reset({
      channelKey: this.channels()[0]?.key ?? '',
      thresholdLow: null,
      thresholdHigh: null,
      durationSeconds: 60,
      cooldownMinutes: 60,
      severity: 'warn',
    });
  }

  create(): void {
    if (this.createForm.invalid || this.busy()) return;
    const value = this.createForm.getRawValue();
    if (value.thresholdLow == null && value.thresholdHigh == null) {
      this.error.set({ code: null, key: 'errors.invalidRule' });
      return;
    }
    this.busy.set(true);
    this.error.set(null);

    this.api
      .createRule(this.deviceId, {
        channelKey: value.channelKey,
        ruleType: 'threshold',
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
          this.load();
        },
        error: (error: unknown) => {
          this.busy.set(false);
          this.error.set(describeApiError(error));
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
          this.load();
        },
        error: (error: unknown) => {
          this.busy.set(false);
          this.error.set(describeApiError(error));
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
          this.load();
        },
        error: (error: unknown) => {
          this.busy.set(false);
          this.error.set(describeApiError(error));
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
        this.load();
      },
      error: (error: unknown) => {
        this.busy.set(false);
        this.error.set(describeApiError(error));
        this.load();
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

  unitFor(channelKey: string | null): string {
    return this.channels().find((channel) => channel.key === channelKey)?.unit ?? '';
  }

  private isArabic(): boolean {
    return this.transloco.getActiveLang() === 'ar';
  }
}
