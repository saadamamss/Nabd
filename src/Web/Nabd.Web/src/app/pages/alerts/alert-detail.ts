import { Component, computed, inject, OnDestroy, OnInit, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { ApiProblem, describeApiError } from '../../core/api/api-error';
import { AlertEvent, AlertRow, AlertsApi } from '../../core/api/alerts.service';
import { AssetRow, TenantAdminApi, UserRow } from '../../core/api/tenant-admin.service';
import {
  actorNameOf,
  assetNameOf,
  eventKey,
  evidenceFacts,
  reasonKey,
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
 * language while free text stays exactly as the admin wrote it (R3). The actions
 * are S4 and deliberately absent.
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
  `,
})
export class AlertDetailPage implements OnInit, OnDestroy {
  private readonly route = inject(ActivatedRoute);
  private readonly api = inject(AlertsApi);
  private readonly admin = inject(TenantAdminApi);
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
