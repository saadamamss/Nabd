import { Component, computed, inject, OnDestroy, OnInit, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { Subscription } from 'rxjs';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { ApiProblem, describeApiError } from '../../core/api/api-error';
import { AlertRow, AlertsApi } from '../../core/api/alerts.service';
import { AssetRow, TenantAdminApi } from '../../core/api/tenant-admin.service';
import { SelectComponent } from '../../core/ui/select';
import {
  AlertFilters,
  AlertLimit,
  AlertSeverityFilter,
  AlertStatusFilter,
  DEFAULT_FILTERS,
  LIMITS,
  SEVERITY_FILTERS,
  STATUS_FILTERS,
  assetNameOf,
  doorQuery,
  hasFilters,
  queryParams,
  readFilters,
  severityKey,
  statusKey,
  titleFor,
} from './alerts-model';

/**
 * P5 spec 05, S2 — the alert list (FR-42).
 *
 * The URL is the filter's only home (E4): changing a filter navigates, and the
 * page loads from `queryParamMap`, so back/forward and a pasted link all take the
 * same road. The list itself is the server's answer, newest first; the cap is the
 * door's (E5), never an invented pagination.
 */
@Component({
  selector: 'app-alerts-page',
  imports: [SelectComponent, TranslocoPipe],
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
                  <td>{{ row.title }}</td>
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

  ngOnInit(): void {
    // Names only decorate the list; a failure must not hide the alerts themselves.
    this.admin.assets(true).subscribe({
      next: (assets) => this.assets.set(assets),
      error: () => this.assets.set([]),
    });

    // One road for the first load, a pasted link, a filter change and back/forward.
    this.paramsSub = this.route.queryParamMap.subscribe((query) => {
      this.filters.set(readFilters(query));
      this.load();
    });
  }

  ngOnDestroy(): void {
    this.paramsSub?.unsubscribe();
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

  load(): void {
    this.loading.set(true);
    this.problem.set(null);

    this.api.list(doorQuery(this.filters())).subscribe({
      next: (rows) => {
        this.alerts.set(rows);
        this.loading.set(false);
      },
      error: (error: unknown) => {
        this.alerts.set([]);
        this.problem.set(describeApiError(error));
        this.loading.set(false);
      },
    });
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
