import { Component, computed, inject, OnDestroy, OnInit, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { ApiProblem, describeApiError } from '../../core/api/api-error';
import { AlertEvent, AlertRow, AlertsApi } from '../../core/api/alerts.service';
import { AssetRow, TenantAdminApi, UserRow } from '../../core/api/tenant-admin.service';
import { AuthService } from '../../core/auth/auth.service';
import { NoticeService } from '../../core/ui/notice.service';
import {
  actorNameOf,
  assetNameOf,
  eventKey,
  evidenceFacts,
  reasonKey,
  reasonTooLong as reasonOverCap,
  RESOLVE_REASON_MAX,
  resolveReasonOf,
  severityKey,
  statusKey,
  titleFor,
} from './alerts-model';
import { QuietHoursBadge } from './quiet-hours-badge';

/**
 * P5 spec 05, S3a — one alert, read honestly.
 *
 * Everything on this page comes from the alert itself: the evidence is the
 * `AlertDetail` copied when the rule crossed (the rule is never re-read, so a
 * later PATCH cannot rewrite history), and the quiet-hours badge is the row's own
 * `QuietHoursSuppressed` — a state of its own, not a decoration of the status.
 *
 * S3b adds the timeline: the audit trail as the door returns it (never re-sorted),
 * actor names resolved from the users list, and reason codes spoken in the reader's
 * language while free text stays exactly as the admin wrote it (R3).
 *
 * S4 adds the two actions (E2/E3): acknowledge is for every role, resolve is a
 * tenant_admin's and goes through the dialog with the 200-char counter. Both draw
 * the row the door answers and re-read the trail it appended — the screen never
 * reports a state the server did not write. A refusal is said where it applies:
 * `invalid_reason` under the field, `invalid_status` beside the buttons plus a
 * fresh read (someone else moved first).
 */
@Component({
  selector: 'app-alert-detail-page',
  imports: [QuietHoursBadge, RouterLink, TranslocoPipe],
  template: `
    <section class="ui-page">
      <header class="ui-head">
        <div>
          <span class="ui-kicker">{{ 'nav.alerts' | transloco }}</span>
          <h1 class="ui-title">{{ title() ?? ('alerts.title' | transloco) }}</h1>
          @if (otherTitle(); as other) {
            <p class="ui-sub alert-other" [attr.dir]="other.dir">
              <span class="alert-other-label">{{ other.labelKey | transloco }}</span>
              {{ other.text }}
            </p>
          }
        </div>
        <a class="ui-btn is-small" routerLink="/alerts">{{ 'alerts.backToList' | transloco }}</a>
      </header>

      @if (problem(); as p) {
        <p class="ui-note is-error">
          {{ p.key | transloco }}
          @if (p.code) {
            <code>{{ p.code }}</code>
          }
        </p>
      }

      @if (loading()) {
        <p class="ui-note">{{ 'common.loading' | transloco }}</p>
      } @else if (alert(); as a) {
        <div class="ui-toolbar alert-badges">
          <span
            class="ui-badge"
            [class.is-warn]="a.severity === 'warn'"
            [class.is-critical]="a.severity === 'critical'"
            [attr.data-severity]="a.severity"
          >
            @if (severityKey(a.severity); as key) {
              {{ key | transloco }}
            } @else {
              {{ a.severity }}
            }
          </span>

          <span class="ui-badge" [class.is-muted]="a.status === 'resolved'" [attr.data-status]="a.status">
            @if (statusKey(a.status); as key) {
              {{ key | transloco }}
            } @else {
              {{ a.status }}
            }
          </span>

          <!-- E9: a row fact, drawn whatever the status and whatever an ack did -->
          @if (a.quietHoursSuppressed) {
            <app-quiet-hours-badge />
          }
        </div>

        @if (a.quietHoursSuppressed) {
          <p class="ui-note" data-quiet-hours-note>{{ 'alerts.quietHoursBody' | transloco }}</p>
        }

        <section class="ui-panel">
          <h2 class="alert-h2">{{ 'alerts.evidence' | transloco }}</h2>
          @if (facts().length) {
            <div class="ui-grid">
              @for (fact of facts(); track fact.key) {
                <div class="alert-fact">
                  <span class="alert-fact-label">{{ fact.key | transloco }}</span>
                  <span class="alert-fact-value" [attr.dir]="fact.numeric ? 'ltr' : null">
                    {{ fact.value }}
                  </span>
                </div>
              }
            </div>
          } @else {
            <p class="ui-hint">{{ 'alerts.noEvidence' | transloco }}</p>
          }
        </section>

        <section class="ui-panel">
          <h2 class="alert-h2">{{ 'alerts.timeline' | transloco }}</h2>

          @if (timelineProblem(); as p) {
            <p class="ui-note is-error">
              {{ p.key | transloco }}
              @if (p.code) {
                <code>{{ p.code }}</code>
              }
            </p>
          } @else if (eventsLoading()) {
            <p class="ui-hint">{{ 'common.loading' | transloco }}</p>
          } @else if (events().length) {
            <ol class="alert-timeline">
              @for (event of events(); track $index) {
                <li class="alert-event" [attr.data-event]="event.event">
                  <span class="alert-event-name">
                    @if (eventKey(event.event); as key) {
                      {{ key | transloco }}
                    } @else {
                      {{ event.event }}
                    }
                  </span>
                  <span class="alert-event-time">{{ absolute(event.at) }}</span>
                  @if (event.actorUserId) {
                    <span class="alert-event-actor">
                      {{ actorName(event.actorUserId) ?? ('alerts.someUser' | transloco) }}
                    </span>
                  }
                  @if (event.reason) {
                    <span class="alert-event-reason" dir="auto">
                      @if (reasonKey(event.reason); as key) {
                        {{ key | transloco }}
                      } @else {
                        {{ event.reason }}
                      }
                    </span>
                  }
                </li>
              }
            </ol>
          } @else {
            <p class="ui-hint">{{ 'alerts.noEvents' | transloco }}</p>
          }
        </section>

        @if (hasActions()) {
          <section class="ui-panel" data-actions>
            <h2 class="alert-h2">{{ 'alerts.actions' | transloco }}</h2>

            @if (actionProblem(); as p) {
              <p class="ui-note is-error" data-action-problem>
                {{ p.key | transloco }}
                @if (p.code) {
                  <code>{{ p.code }}</code>
                }
              </p>
            }

            <div class="ui-actions">
              @if (a.status === 'open') {
                <button
                  class="ui-btn"
                  type="button"
                  data-action="ack"
                  [disabled]="acking()"
                  (click)="acknowledge()"
                >
                  {{ (acking() ? 'alerts.acknowledging' : 'alerts.acknowledge') | transloco }}
                </button>
              }

              <!-- E2: resolve is the tenant_admin's; a viewer never sees the door -->
              @if (canResolve()) {
                <button class="ui-btn is-primary" type="button" data-action="resolve" (click)="openResolve()">
                  {{ 'alerts.resolve' | transloco }}
                </button>
              }
            </div>
          </section>
        }

        @if (resolveOpen()) {
          <div class="ui-overlay" role="presentation" (keydown.escape)="closeResolve()">
            <div class="ui-dialog" role="dialog" aria-modal="true" aria-labelledby="resolve-alert">
              <h2 id="resolve-alert">{{ 'alerts.resolveTitle' | transloco }}</h2>
              <p class="ui-hint">{{ 'alerts.resolveBody' | transloco }}</p>

              <label class="ui-field">
                <span>{{ 'alerts.resolveReason' | transloco }}</span>
                <textarea
                  class="alert-reason-box"
                  rows="3"
                  [value]="reason()"
                  [attr.aria-invalid]="reasonTooLong() || reasonProblem() ? 'true' : null"
                  (input)="reason.set($any($event.target).value)"
                ></textarea>
                <small class="alert-counter" [class.is-over]="reasonTooLong()" data-reason-counter>
                  {{ reasonLength() }} / {{ resolveMax }}
                </small>
              </label>

              <p class="ui-hint">{{ 'alerts.resolveReasonHint' | transloco }}</p>

              @if (reasonProblem(); as p) {
                <p class="ui-note is-error" data-reason-problem>
                  {{ p.key | transloco }}
                  @if (p.code) {
                    <code>{{ p.code }}</code>
                  }
                </p>
              }

              <div class="ui-actions">
                <button class="ui-btn" type="button" (click)="closeResolve()" [disabled]="resolving()">
                  {{ 'common.cancel' | transloco }}
                </button>
                <button
                  class="ui-btn is-primary"
                  type="button"
                  data-action="confirm-resolve"
                  [disabled]="resolving()"
                  (click)="resolve()"
                >
                  {{ (resolving() ? 'alerts.resolving' : 'alerts.resolve') | transloco }}
                </button>
              </div>
            </div>
          </div>
        }

        <section class="ui-panel">
          <h2 class="alert-h2">{{ 'alerts.state' | transloco }}</h2>
          <div class="ui-grid">
            <div class="alert-fact">
              <span class="alert-fact-label">{{ 'alerts.colAsset' | transloco }}</span>
              <span class="alert-fact-value">
                {{ assetName(a.assetId) || ('alerts.assetUnknown' | transloco) }}
              </span>
            </div>
            <div class="alert-fact">
              <span class="alert-fact-label">{{ 'alerts.colOpened' | transloco }}</span>
              <span class="alert-fact-value">{{ absolute(a.openedAt) }}</span>
            </div>
            <div class="alert-fact">
              <span class="alert-fact-label">{{ 'alerts.acknowledgedAt' | transloco }}</span>
              <span class="alert-fact-value">
                {{ a.acknowledgedAt ? absolute(a.acknowledgedAt) : ('alerts.notAcknowledged' | transloco) }}
              </span>
            </div>
            <div class="alert-fact">
              <span class="alert-fact-label">{{ 'alerts.resolvedAt' | transloco }}</span>
              <span class="alert-fact-value">
                {{ a.resolvedAt ? absolute(a.resolvedAt) : ('alerts.notResolved' | transloco) }}
              </span>
            </div>
          </div>
        </section>
      }
    </section>
  `,
  styles: `
    .alert-other {
      margin-block-start: 0.35rem;
      font-size: 0.875rem;
    }

    .alert-other-label {
      display: block;
      font-size: 0.75rem;
      font-weight: 600;
    }

    .alert-h2 {
      margin-block: 0 0.75rem;
      font-size: 0.95rem;
      font-weight: 700;
    }

    .alert-fact {
      display: flex;
      flex-direction: column;
      gap: 0.2rem;
      min-inline-size: 0;
    }

    .alert-fact-label {
      color: var(--app-text-muted);
      font-size: 0.8125rem;
      font-weight: 600;
    }

    .alert-fact-value {
      font-variant-numeric: tabular-nums;
      overflow-wrap: anywhere;
    }

    .alert-timeline {
      display: flex;
      flex-direction: column;
      gap: 0.5rem;
      margin: 0;
      padding: 0;
      list-style: none;
    }

    .alert-event {
      display: flex;
      flex-wrap: wrap;
      align-items: baseline;
      gap: 0.5rem;
      border-inline-start: 2px solid var(--app-border);
      padding-inline-start: 0.6rem;
    }

    .alert-event-name {
      font-weight: 600;
    }

    .alert-event-time {
      color: var(--app-text-muted);
      font-size: 0.875rem;
      font-variant-numeric: tabular-nums;
    }

    .alert-event-actor,
    .alert-event-reason {
      font-size: 0.875rem;
      overflow-wrap: anywhere;
    }

    /* the shared .ui-field styles an input; the reason is a textarea */
    .alert-reason-box {
      inline-size: 100%;
      padding-block: 0.6rem;
      padding-inline: 0.75rem;
      border: 1px solid var(--app-border-strong);
      border-radius: var(--app-radius-sm);
      background: var(--app-surface);
      color: var(--app-text);
      font: inherit;
      resize: vertical;
    }

    .alert-counter {
      text-align: end;
      font-variant-numeric: tabular-nums;
    }

    .alert-counter.is-over {
      color: var(--app-alert);
      font-weight: 700;
    }
  `,
})
export class AlertDetailPage implements OnInit, OnDestroy {
  private readonly route = inject(ActivatedRoute);
  private readonly api = inject(AlertsApi);
  private readonly admin = inject(TenantAdminApi);
  private readonly auth = inject(AuthService);
  private readonly notices = inject(NoticeService);
  private readonly transloco = inject(TranslocoService);

  readonly alert = signal<AlertRow | null>(null);
  readonly assets = signal<AssetRow[]>([]);
  readonly loading = signal(true);
  readonly problem = signal<ApiProblem | null>(null);

  /** The audit trail, in the door's order — rendered, never re-sorted. */
  readonly events = signal<AlertEvent[]>([]);
  readonly eventsLoading = signal(false);
  readonly timelineProblem = signal<ApiProblem | null>(null);
  /** Names decorate the timeline; a failed users read must not hide the trail. */
  readonly users = signal<UserRow[]>([]);

  /** S4: the two actions and the refusals they can collect. */
  readonly acking = signal(false);
  readonly resolving = signal(false);
  readonly resolveOpen = signal(false);
  readonly reason = signal('');
  /** A refusal about the dialog's field, said under the field. */
  readonly reasonProblem = signal<ApiProblem | null>(null);
  /** A refusal about the action itself, said beside the buttons. */
  readonly actionProblem = signal<ApiProblem | null>(null);

  readonly resolveMax = RESOLVE_REASON_MAX;

  /**
   * E2: ack is every role's; resolve belongs to tenant_admin (and the platform
   * admin the policy ladder also admits). The button is hidden — and the method
   * still refuses locally, so a forced call cannot reach a door it may not open.
   */
  readonly canResolve = computed(() => {
    const role = this.auth.user()?.role;
    return role === 'tenant_admin' || role === 'platform_admin';
  });

  /**
   * Nothing left to do on a resolved alert — and nothing for a viewer on an
   * acknowledged one: the panel is drawn only when a button would be in it,
   * never as a heading with an empty row beneath it.
   */
  readonly hasActions = computed(() => {
    const row = this.alert();
    if (row === null || row.status === 'resolved') return false;
    return row.status === 'open' || this.canResolve();
  });

  /** Counted the server's way: what is left after the trim. */
  readonly reasonLength = computed(() => resolveReasonOf(this.reason())?.length ?? 0);

  readonly reasonTooLong = computed(() => reasonOverCap(this.reason()));

  /** Both titles ship with the alert; the locale picks one and shows the other. */
  readonly title = computed(() => {
    const row = this.alert();
    return row ? titleFor(row, this.transloco.activeLang()) : null;
  });

  readonly otherTitle = computed(() => {
    const row = this.alert();
    if (!row) return null;

    return this.transloco.activeLang() === 'ar'
      ? { labelKey: 'alerts.titleInEn', text: row.titleEn, dir: 'ltr' }
      : { labelKey: 'alerts.titleInAr', text: row.titleAr, dir: 'rtl' };
  });

  /** The stored evidence, formatted for the active locale (never re-derived). */
  readonly facts = computed(() => evidenceFacts(this.alert()?.detail, this.transloco.activeLang()));

  private paramsSub: Subscription | null = null;
  private usersFor = '';

  ngOnInit(): void {
    // A name decorates the header only; a failure must not hide the alert itself.
    this.admin.assets(true).subscribe({
      next: (assets) => this.assets.set(assets),
      error: () => this.assets.set([]),
    });

    this.paramsSub = this.route.paramMap.subscribe((params) => {
      const alertId = params.get('alertId');
      if (alertId) this.load(alertId);
    });
  }

  ngOnDestroy(): void {
    this.paramsSub?.unsubscribe();
  }

  load(alertId: string): void {
    this.loading.set(true);
    this.problem.set(null);
    this.events.set([]);
    this.timelineProblem.set(null);

    this.api.get(alertId).subscribe({
      next: (row) => {
        this.alert.set(row);
        this.loading.set(false);
        this.loadEvents(alertId);
        this.loadUsers(row.tenantId);
      },
      error: (error: unknown) => {
        // A gone or cross-tenant alert is 404 by the P1 law; say so, keep the way back.
        this.alert.set(null);
        this.problem.set(describeApiError(error));
        this.loading.set(false);
      },
    });
  }

  /**
   * The trail is its own read: a failure in it is said in the timeline section
   * and never takes the alert down with it (that evidence is already on screen).
   */
  private loadEvents(alertId: string): void {
    this.eventsLoading.set(true);

    this.api.events(alertId).subscribe({
      next: (events) => {
        this.events.set(events);
        this.eventsLoading.set(false);
      },
      error: (error: unknown) => {
        this.events.set([]);
        this.timelineProblem.set(describeApiError(error));
        this.eventsLoading.set(false);
      },
    });
  }

  /** The door answers the row it wrote: draw it, then re-read the trail it appended. */
  private drawWrittenRow(row: AlertRow): void {
    this.alert.set(row);
    this.loadEvents(row.alertId);
  }

  /**
   * A refusal is said; when the alert itself moved, the screen is re-read so the
   * buttons match the server. A failed re-read is silent — the message already
   * explains what happened, and the last known state is still honest evidence.
   */
  private fail(problem: ApiProblem, alertId: string): void {
    this.actionProblem.set(problem);

    if (problem.code === 'alerting:invalid_status') {
      this.api.get(alertId).subscribe({
        next: (row) => this.drawWrittenRow(row),
        error: () => undefined,
      });
    }
  }

  /** One users read per tenant; the alert's own tenant is the caller's (RLS). */
  private loadUsers(tenantId: string): void {
    if (!tenantId || this.usersFor === tenantId) return;
    this.usersFor = tenantId;

    this.admin.users(tenantId).subscribe({
      next: (users) => this.users.set(users),
      error: () => this.users.set([]),
    });
  }

  severityKey(severity: string): string | null {
    return severityKey(severity);
  }

  statusKey(status: string): string | null {
    return statusKey(status);
  }

  assetName(assetId: string): string | null {
    return assetNameOf(this.assets(), assetId);
  }

  /**
   * Acknowledge: one click, the door's row, a localized toast. A refusal about
   * the state (`invalid_status`) means someone else moved first — say it and read
   * the truth again instead of leaving a stale button on screen.
   */
  acknowledge(): void {
    const row = this.alert();
    if (!row || this.acking()) return;

    this.acking.set(true);
    this.actionProblem.set(null);

    this.api.acknowledge(row.alertId).subscribe({
      next: (updated) => {
        this.acking.set(false);
        this.drawWrittenRow(updated);
        this.notices.show('toast.alertAcknowledged', 'success');
      },
      error: (error: unknown) => {
        this.acking.set(false);
        this.fail(describeApiError(error), row.alertId);
      },
    });
  }

  openResolve(): void {
    if (!this.canResolve()) return;
    this.reason.set('');
    this.reasonProblem.set(null);
    this.actionProblem.set(null);
    this.resolveOpen.set(true);
  }

  closeResolve(): void {
    if (this.resolving()) return;
    this.resolveOpen.set(false);
    this.reasonProblem.set(null);
  }

  /**
   * Resolve with an optional reason (E3). A blank reason sends **no** parameter, so
   * the server writes its own "manual" — the client never invents a code. A reason
   * over the cap is refused here, before any request, with the same message the
   * server would send.
   */
  resolve(): void {
    const row = this.alert();
    if (!row || this.resolving()) return;

    if (!this.canResolve()) {
      this.actionProblem.set({ code: 'identity:forbidden', key: 'errors.forbidden' });
      return;
    }

    if (this.reasonTooLong()) {
      this.reasonProblem.set({ code: 'alerting:invalid_reason', key: 'errors.alertReason' });
      return;
    }

    this.resolving.set(true);
    this.reasonProblem.set(null);
    this.actionProblem.set(null);

    this.api.resolve(row.alertId, this.reason()).subscribe({
      next: (updated) => {
        this.resolving.set(false);
        this.resolveOpen.set(false);
        this.drawWrittenRow(updated);
        this.notices.show('toast.alertResolved', 'success');
      },
      error: (error: unknown) => {
        this.resolving.set(false);
        const problem = describeApiError(error);

        // field-anchored: the reason itself was refused, the dialog stays open
        if (problem.code === 'alerting:invalid_reason') {
          this.reasonProblem.set(problem);
          return;
        }

        // someone else resolved first: close the stale dialog, say so, re-read
        if (problem.code === 'alerting:invalid_status') {
          this.resolveOpen.set(false);
        }
        this.fail(problem, row.alertId);
      },
    });
  }

  eventKey(event: string): string | null {
    return eventKey(event);
  }

  reasonKey(reason: string): string | null {
    return reasonKey(reason);
  }

  actorName(actorUserId: string): string | null {
    return actorNameOf(this.users(), actorUserId);
  }

  absolute(iso: string): string {
    return new Intl.DateTimeFormat(this.transloco.activeLang(), {
      dateStyle: 'medium',
      timeStyle: 'short',
    }).format(new Date(iso));
  }
}
