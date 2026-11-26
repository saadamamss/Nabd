import { Component, computed, inject, OnDestroy, OnInit, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { ApiProblem, describeApiError } from '../../core/api/api-error';
import { AlertRow, AlertsApi } from '../../core/api/alerts.service';
import { AlertChange } from '../../core/api/dashboard.service';
import { AssetRow, TenantAdminApi } from '../../core/api/tenant-admin.service';
import { DashboardSocket } from '../../core/realtime/dashboard.socket';
import { SelectComponent } from '../../core/ui/select';
import { QuietHoursBadge } from './quiet-hours-badge';
import {
  AlertFilters,
  AlertLimit,
  AlertSeverityFilter,
  AlertStatusFilter,
  DEFAULT_FILTERS,
  LIMITS,
  SEVERITY_FILTERS,
  STATUS_FILTERS,
  applyHint,
  assetNameOf,
  doorQuery,
  focusedAlertId,
  hasFilters,
  matchesFilters,
  placeRow,
  queryParams,
  readFilters,
  severityKey,
  statusKey,
  titleFor,
} from './alerts-model';

/**
 * P5 spec 05, S2 + S5 — the alert list (FR-42).
 *
 * The URL is the filter's only home (E4): changing a filter navigates, and the
 * page loads from `queryParamMap`, so back/forward and a pasted link all take the
 * same road. The list itself is the server's answer, newest first; the cap is the
 * door's (E5), never an invented pagination.
 *
 * Live (S5): spec 03's `alerts` channel moves a row from the hint's committed
 * facts and then re-reads that row from the list's own door — the page never
 * grows a row out of a hint. When the socket is not live, a quiet 30 s re-read
 * stands in, so the list still ages without a reload or an error storm.
 */
@Component({
  selector: 'app-alerts-page',
  imports: [QuietHoursBadge, RouterLink, SelectComponent, TranslocoPipe],
  template: `
    <section class="ui-page">
      <header class="ui-head">
        <div>
          <span class="ui-kicker">{{ 'nav.alerts' | transloco }}</span>
          <h1 class="ui-title">{{ 'alerts.title' | transloco }}</h1>
          <p class="ui-sub">{{ 'alerts.body' | transloco }}</p>
        </div>
      </header>

      <div class="ui-toolbar">
        <label class="ui-field">
          <span>{{ 'alerts.filterStatus' | transloco }}</span>
          <app-select
            [options]="statusOptions"
            [value]="filters().status"
            [ariaLabel]="'alerts.filterStatus' | transloco"
            (valueChange)="setStatus($event)"
          />
        </label>

        <label class="ui-field">
          <span>{{ 'alerts.filterSeverity' | transloco }}</span>
          <app-select
            [options]="severityOptions"
            [value]="filters().severity"
            [ariaLabel]="'alerts.filterSeverity' | transloco"
            (valueChange)="setSeverity($event)"
          />
        </label>

        <label class="ui-field">
          <span>{{ 'alerts.filterAsset' | transloco }}</span>
          <app-select
            [options]="assetOptions()"
            [value]="filters().assetId || 'all'"
            [ariaLabel]="'alerts.filterAsset' | transloco"
            (valueChange)="setAsset($event)"
          />
        </label>

        <label class="ui-field">
          <span>{{ 'alerts.filterLimit' | transloco }}</span>
          <app-select
            [options]="limitOptions"
            [value]="filters().limit.toString()"
            [ariaLabel]="'alerts.filterLimit' | transloco"
            (valueChange)="setLimit($event)"
          />
        </label>

        @if (filtersActive()) {
          <button class="ui-btn is-small" type="button" (click)="reset()">
            {{ 'alerts.reset' | transloco }}
          </button>
        }
      </div>

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
      } @else if (!rows().length) {
        <div class="ui-panel ui-empty">
          <p>{{ (filtersActive() ? 'alerts.emptyFiltered' : 'alerts.empty') | transloco }}</p>
          @if (filtersActive()) {
            <button class="ui-btn is-small" type="button" (click)="reset()">
              {{ 'alerts.reset' | transloco }}
            </button>
          }
        </div>
      } @else {
        <div class="ui-table-wrap">
          <table class="ui-table">
            <thead>
              <tr>
                <th scope="col">{{ 'alerts.colSeverity' | transloco }}</th>
                <th scope="col">{{ 'alerts.colStatus' | transloco }}</th>
                <th scope="col">{{ 'alerts.colAlert' | transloco }}</th>
                <th scope="col">{{ 'alerts.colAsset' | transloco }}</th>
                <th scope="col">{{ 'alerts.colOpened' | transloco }}</th>
                <th scope="col">{{ 'alerts.colAcknowledged' | transloco }}</th>
              </tr>
            </thead>
            <tbody>
              @for (row of rows(); track row.alertId) {
                <tr [attr.data-status]="row.status">
                  <td>
                    <span
                      class="ui-badge"
                      [class.is-warn]="row.severity === 'warn'"
                      [class.is-critical]="row.severity === 'critical'"
                      [attr.data-severity]="row.severity"
                    >
                      @if (row.severity === 'warn' || row.severity === 'critical') {
                        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" class="alert-icon">
                          <path d="M12 4 3.8 19h16.4L12 4Z" />
                          <path d="M12 10v4" />
                          <path d="M12 17h.01" />
                        </svg>
                      } @else {
                        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" class="alert-icon">
                          <circle cx="12" cy="12" r="8.5" />
                          <path d="M12 11v5" />
                          <path d="M12 8h.01" />
                        </svg>
                      }
                      @if (severityKey(row.severity); as key) {
                        {{ key | transloco }}
                      } @else {
                        {{ row.severity }}
                      }
                    </span>
                  </td>
                  <td>
                    <span
                      class="ui-badge"
                      [class.is-muted]="row.status === 'resolved'"
                      [attr.data-status]="row.status"
                    >
                      @if (statusKey(row.status); as key) {
                        {{ key | transloco }}
                      } @else {
                        {{ row.status }}
                      }
                    </span>
                  </td>
                  <td>
                    <a [routerLink]="['/alerts', row.alertId]">{{ row.title }}</a>
                    @if (row.quietHoursSuppressed) {
                      <div class="alert-flag"><app-quiet-hours-badge /></div>
                    }
                  </td>
                  <td>{{ assetName(row.assetId) || ('alerts.assetUnknown' | transloco) }}</td>
                  <td>{{ absolute(row.openedAt) }}</td>
                  <td>
                    {{ row.acknowledgedAt ? absolute(row.acknowledgedAt) : ('alerts.none' | transloco) }}
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>

        <p class="ui-hint">
          {{ 'alerts.showingLatest' | transloco: { count: rows().length, limit: filters().limit } }}
        </p>
      }
    </section>
  `,
  styles: `
    .alert-flag {
      margin-block-start: 0.3rem;
    }

    .alert-icon {
      flex: none;
      inline-size: 0.85rem;
      block-size: 0.85rem;
    }
  `,
})
export class AlertsPage implements OnInit, OnDestroy {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly api = inject(AlertsApi);
  private readonly admin = inject(TenantAdminApi);
  private readonly socket = inject(DashboardSocket);
  private readonly transloco = inject(TranslocoService);

  readonly filters = signal<AlertFilters>(DEFAULT_FILTERS);
  readonly alerts = signal<AlertRow[]>([]);
  readonly assets = signal<AssetRow[]>([]);
  readonly loading = signal(true);
  readonly problem = signal<ApiProblem | null>(null);

  readonly statusOptions = STATUS_FILTERS.map((value) => ({
    value,
    labelKey: value === 'all' ? 'alerts.filterAll' : (statusKey(value) as string),
  }));

  readonly severityOptions = SEVERITY_FILTERS.map((value) => ({
    value,
    labelKey: value === 'all' ? 'alerts.filterAllSeverities' : (severityKey(value) as string),
  }));

  readonly limitOptions = LIMITS.map((value) => ({ value: String(value), label: String(value) }));

  /** Archived assets stay in the list: an alert keeps its asset's name. */
  readonly assetOptions = computed(() => [
    { value: 'all', labelKey: 'alerts.filterAllAssets' },
    ...this.assets().map((asset) => ({ value: asset.assetId, label: asset.name })),
  ]);

  /** Localized rows: a language switch must re-pick the title without a reload. */
  readonly rows = computed(() => {
    const lang = this.transloco.activeLang();
    return this.alerts().map((row) => ({
      ...row,
      title: titleFor(row, lang),
    }));
  });

  readonly filtersActive = computed(() => hasFilters(this.filters()));

  private paramsSub: Subscription | null = null;
  private fallback: ReturnType<typeof setInterval> | null = null;
  private coalesce: ReturnType<typeof setTimeout> | null = null;
  /** Alert ids pushed since the last door read — a burst becomes a few reads. */
  private readonly pending = new Set<string>();

  ngOnInit(): void {
    // Names only decorate the list; a failure must not hide the alerts themselves.
    this.admin.assets(true).subscribe({
      next: (assets) => this.assets.set(assets),
      error: () => this.assets.set([]),
    });

    // One road for the first load, a pasted link, a filter change and back/forward.
    this.paramsSub = this.route.queryParamMap.subscribe((query) => {
      // The dashboard's "open alert" arrives here with `?alertId=` (spec 03); the
      // alert's own address is the detail route, so forward it instead of ignoring it.
      const focused = focusedAlertId(query);
      if (focused) {
        void this.router.navigate(['/alerts', focused], { replaceUrl: true });
        return;
      }

      this.filters.set(readFilters(query));
      this.load();
    });

    // Spec 03's `alerts` channel (E6). The page owns the connection while it is
    // mounted; a push never reloads the list, it moves one row and re-reads it.
    this.socket.start({
      onStatus: () => undefined,
      onAlert: (change) => this.applyChange(change),
      onResync: () => this.load(true),
    });

    // The socket is a hint lane, not a dependency: while it is not live the page
    // still has to age, so a quiet re-read every 30 s stands in. A quiet read
    // never blinks the table and never raises a banner — an outage must not
    // become an error storm (test fact 10).
    this.fallback = setInterval(() => {
      if (this.socket.state() !== 'live' && document.visibilityState === 'visible') {
        this.load(true);
      }
    }, 30_000);
  }

  ngOnDestroy(): void {
    this.paramsSub?.unsubscribe();
    if (this.fallback) clearInterval(this.fallback);
    if (this.coalesce) clearTimeout(this.coalesce);
    this.socket.stop();
  }

  setStatus(value: string): void {
    this.navigate({ ...this.filters(), status: value as AlertStatusFilter });
  }

  setSeverity(value: string): void {
    this.navigate({ ...this.filters(), severity: value as AlertSeverityFilter });
  }

  setAsset(value: string): void {
    this.navigate({ ...this.filters(), assetId: value === 'all' ? '' : value });
  }

  setLimit(value: string): void {
    this.navigate({ ...this.filters(), limit: (value === '500' ? 500 : 100) as AlertLimit });
  }

  reset(): void {
    this.navigate({ ...DEFAULT_FILTERS, limit: this.filters().limit });
  }

  /**
   * Reads the list for the current filters. `quiet` is the fallback/reconnect
   * read: it keeps the rows and the screen as they are, so a failed stand-in read
   * cannot erase a list the last good answer produced.
   */
  load(quiet = false): void {
    if (!quiet) {
      this.loading.set(true);
      this.problem.set(null);
    }

    this.api.list(doorQuery(this.filters())).subscribe({
      next: (rows) => {
        this.alerts.set(rows);
        this.problem.set(null);
        this.loading.set(false);
      },
      error: (error: unknown) => {
        if (quiet) return;
        this.alerts.set([]);
        this.problem.set(describeApiError(error));
        this.loading.set(false);
      },
    });
  }

  /**
   * A hub push (E6). The hint's own facts are committed, so a row already on
   * screen moves at once — no reload, no invented ack time. The door's row is
   * then read and merged: it alone knows the acknowledgment/resolution times and
   * the quiet-hours flag, and it decides whether the row still matches the
   * filter at all.
   *
   * `kind` (`raised`/`changed`) needs no branch: it says why a row moved, and the
   * answer is the same either way — move the row you have, read the row you do not.
   */
  applyChange(change: AlertChange): void {
    const hint = change.alert;
    const filters = this.filters();

    this.alerts.update((rows) =>
      applyHint(rows, hint).filter((row) => matchesFilters(row, filters)),
    );

    this.pending.add(hint.alertId);
    if (this.coalesce) return;

    this.coalesce = setTimeout(() => this.readPending(), 40);
  }

  /** One door read per pushed alert, after a burst has settled (B11's habit). */
  private readPending(): void {
    const ids = [...this.pending];
    this.pending.clear();
    this.coalesce = null;

    for (const alertId of ids) {
      this.api.get(alertId).subscribe({
        next: (row) => {
          const filters = this.filters();
          this.alerts.update((rows) =>
            matchesFilters(row, filters)
              ? placeRow(rows, row, filters.limit)
              : rows.filter((candidate) => candidate.alertId !== row.alertId),
          );
        },
        // A hint whose row cannot be read is a missed hint, not a screen error.
        error: () => undefined,
      });
    }
  }

  statusKey(status: string): string | null {
    return statusKey(status);
  }

  severityKey(severity: string): string | null {
    return severityKey(severity);
  }

  assetName(assetId: string): string | null {
    return assetNameOf(this.assets(), assetId);
  }

  absolute(iso: string): string {
    return new Intl.DateTimeFormat(this.transloco.activeLang(), {
      dateStyle: 'medium',
      timeStyle: 'short',
    }).format(new Date(iso));
  }

  /** The filters live in the URL; this page never keeps a second copy of them. */
  private navigate(filters: AlertFilters): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: queryParams(filters),
    });
  }
}
