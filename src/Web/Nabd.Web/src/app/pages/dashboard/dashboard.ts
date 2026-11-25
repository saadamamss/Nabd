import { Component, computed, inject, OnDestroy, OnInit, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { forkJoin } from 'rxjs';
import { ApiProblem, describeApiError } from '../../core/api/api-error';
import { AssetStatus, AssetStatusRow, DashboardApi } from '../../core/api/dashboard.service';
import { AssetRow, TenantAdminApi } from '../../core/api/tenant-admin.service';
import { DashboardSocket, RealtimeState } from '../../core/realtime/dashboard.socket';

interface Card {
  asset: AssetRow;
  status: AssetStatusRow | null;
}

/**
 * P5 spec 03 — the live dashboard (FR-40).
 *
 * The server's snapshot is the truth; hub pushes are hints. Every event triggers a
 * server-side re-read, so this page can only ever show a *freshly derived* state —
 * a dropped socket leaves the last snapshot on screen and says "reconnecting",
 * a reconnect refetches, and a visible tab refetches every 60 s so "last seen"
 * cannot age into a lie (B5, B11).
 */
@Component({
  selector: 'app-dashboard-page',
  imports: [RouterLink, TranslocoPipe],
  template: `
    <section class="ui-page">
      <header class="ui-head">
        <div>
          <span class="ui-kicker">{{ 'nav.overview' | transloco }}</span>
          <h1 class="ui-title">{{ 'dashboard.title' | transloco }}</h1>
          <p class="ui-sub">{{ 'dashboard.body' | transloco }}</p>
        </div>
        <span class="dash-link" [attr.data-state]="connection()" role="status" aria-live="polite">
          <span class="dash-dot" aria-hidden="true"></span>
          {{ connectionKey() | transloco }}
        </span>
      </header>

      @if (error(); as problem) {
        <p class="ui-note is-error">
          {{ problem.key | transloco }}
          @if (problem.code) {
            <code>{{ problem.code }}</code>
          }
        </p>
      }

      @if (loading()) {
        <p class="ui-note">{{ 'common.loading' | transloco }}</p>
      } @else if (!assets().length) {
        <p class="ui-panel ui-empty">{{ 'dashboard.empty' | transloco }}</p>
      } @else {
        <p class="ui-hint dash-summary">{{ summary() }}</p>

        <div class="ui-toolbar">
          <button
            type="button"
            class="ui-btn is-small"
            [class.is-primary]="filter() === 'all'"
            [attr.aria-pressed]="filter() === 'all'"
            (click)="filter.set('all')"
          >
            {{ 'dashboard.filterAll' | transloco }}
          </button>
          <button
            type="button"
            class="ui-btn is-small"
            [class.is-primary]="filter() === 'problems'"
            [attr.aria-pressed]="filter() === 'problems'"
            (click)="filter.set('problems')"
          >
            {{ 'dashboard.filterProblems' | transloco }}
          </button>
          <span class="ui-hint">{{ 'dashboard.liveHint' | transloco }}</span>
        </div>

        @if (filter() === 'problems' && !visibleCards().length) {
          <p class="ui-panel ui-empty">{{ 'dashboard.noProblems' | transloco }}</p>
        } @else {
          <div class="dash-grid">
            @for (card of visibleCards(); track card.asset.assetId) {
              <article class="dash-card" [attr.data-status]="card.status?.status ?? 'unknown'">
                <header class="dash-card-head">
                  <h2 class="dash-name">{{ card.asset.name }}</h2>
                  <span class="ui-hint">{{ card.asset.location || ('assets.none' | transloco) }}</span>
                </header>

                <p class="dash-pill" [attr.data-status]="card.status?.status ?? 'unknown'">
                  @switch (card.status?.status) {
                    @case ('healthy') {
                      <svg data-status-icon="healthy" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                        <circle cx="12" cy="12" r="8.5" />
                        <path d="M8.5 12.2l2.5 2.5 4.7-5" />
                      </svg>
                    }
                    @case ('warning') {
                      <svg data-status-icon="warning" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                        <path d="M12 4 3.8 19h16.4L12 4Z" />
                        <path d="M12 10v4" />
                        <path d="M12 17h.01" />
                      </svg>
                    }
                    @case ('alert') {
                      <svg data-status-icon="alert" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                        <path d="M18 8.5a6 6 0 1 0-12 0c0 6.5-2.5 7.5-2.5 7.5h17S18 15 18 8.5Z" />
                        <path d="M13.7 19.5a2 2 0 0 1-3.4 0" />
                      </svg>
                    }
                    @case ('offline') {
                      <svg data-status-icon="offline" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                        <path d="M5 5 19 19" />
                        <circle cx="12" cy="12" r="8.5" />
                      </svg>
                    }
                    @default {
                      <svg data-status-icon="unknown" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                        <circle cx="12" cy="12" r="8.5" />
                        <path d="M9.5 9.5a2.5 2.5 0 1 1 3.4 2.3c-.6.3-.9.8-.9 1.5" />
                        <path d="M12 17h.01" />
                      </svg>
                    }
                  }
                  <span>{{ statusKey(card.status) | transloco }}</span>
                </p>

                <p class="ui-hint" [attr.title]="absolute(card.status?.lastSeenAt)">
                  {{ lastSeen(card.status?.lastSeenAt) }}
                </p>

                @if (card.status?.since; as since) {
                  <p class="ui-hint">{{ 'dashboard.since' | transloco: { time: absolute(since) } }}</p>
                }

                @if (card.status?.alertId; as alertId) {
                  <a class="ui-btn is-small dash-open" [routerLink]="['/alerts']" [queryParams]="{ alertId }">
                    {{ 'dashboard.openAlert' | transloco }}
                  </a>
                }
              </article>
            }
          </div>
        }
      }
    </section>
  `,
})
export class DashboardPage implements OnInit, OnDestroy {
  private readonly api = inject(DashboardApi);
  private readonly admin = inject(TenantAdminApi);
  private readonly socket = inject(DashboardSocket);
  private readonly transloco = inject(TranslocoService);

  readonly assets = signal<AssetRow[]>([]);
  readonly statuses = signal<Record<string, AssetStatusRow>>({});
  readonly loading = signal(true);
  readonly error = signal<ApiProblem | null>(null);
  readonly filter = signal<'all' | 'problems'>('all');
  /** Ticks so "last seen" ages on screen without inventing a newer timestamp. */
  readonly now = signal(Date.now());

  readonly connection = this.socket.state;

  readonly cards = computed<Card[]>(() => {
    const byAsset = this.statuses();
    return this.assets().map((asset) => ({ asset, status: byAsset[asset.assetId] ?? null }));
  });

  readonly visibleCards = computed<Card[]>(() => {
    const cards = this.cards();
    return this.filter() === 'problems'
      ? cards.filter((card) => card.status?.status !== 'healthy')
      : cards;
  });

  readonly counts = computed(() => {
    const counts: Record<AssetStatus, number> = { healthy: 0, warning: 0, alert: 0, offline: 0 };
    for (const card of this.cards()) {
      // Guarded at runtime: a server value outside the four words must not
      // become an `undefined`/`NaN` in the summary (the UI never invents a count).
      const value = card.status?.status;
      if (value && Object.hasOwn(counts, value)) counts[value] += 1;
    }
    return counts;
  });

  readonly summary = computed(() => {
    const counts = this.counts();
    // `translate()` is a plain call, not a signal: this computed would otherwise
    // stay memoized in the old language until a reload. `activeLang` IS a signal,
    // so reading it here recomputes the line the moment the user switches.
    this.transloco.activeLang();
    return this.transloco.translate('dashboard.summary', counts);
  });

  private clock: ReturnType<typeof setInterval> | null = null;
  private refetch: ReturnType<typeof setInterval> | null = null;
  private coalesce: ReturnType<typeof setTimeout> | null = null;
  private readonly pending = new Map<string, AssetStatusRow>();

  private readonly onVisibility = (): void => {
    // Coming back to a visible tab is the first honest moment to refresh.
    if (this.visible()) {
      this.now.set(Date.now());
      this.refresh();
    }
  };

  ngOnInit(): void {
    this.refresh();

    this.socket.start({
      onStatus: (row) => this.applyStatus(row),
      // The `alerts` hint is spec 05's lane; the dashboard's truth is `asset-status`.
      onAlert: () => undefined,
      onResync: () => this.refresh(),
    });

    this.clock = setInterval(() => this.now.set(Date.now()), 30_000);
    this.refetch = setInterval(() => {
      if (this.visible()) this.refresh();
    }, 60_000);
    document.addEventListener('visibilitychange', this.onVisibility);
  }

  ngOnDestroy(): void {
    if (this.clock) clearInterval(this.clock);
    if (this.refetch) clearInterval(this.refetch);
    if (this.coalesce) clearTimeout(this.coalesce);
    document.removeEventListener('visibilitychange', this.onVisibility);
    this.socket.stop();
  }

  /** One snapshot for the whole grid; a quiet refresh never blinks the cards. */
  refresh(): void {
    if (!this.assets().length) this.loading.set(true);

    // A new attempt invalidates the old verdict: a stale banner must not sit
    // beside fresh cards. A lane that fails again re-sets it below, so a real
    // failure is never hidden by the other lane's success.
    this.error.set(null);

    // Both halves land together: cards are (asset + status) pairs, so applying
    // one list before the other could show a newly created asset as "Unknown"
    // or a stale status map — a half-truth the UI must not render.
    forkJoin({ statuses: this.api.statuses(), assets: this.admin.assets() }).subscribe({
      next: ({ statuses, assets }) => {
        this.statuses.set(Object.fromEntries(statuses.map((row) => [row.assetId, row])));
        this.assets.set(assets);
        this.loading.set(false);
      },
      error: (error: unknown) => {
        this.error.set(describeApiError(error));
        this.loading.set(false);
      },
    });
  }

  /**
   * Hub pushes coalesce (spec 03 fact 11): a burst for one asset becomes a few
   * renders, never fifty. 40 ms is shorter than the eye, longer than a burst.
   */
  applyStatus(row: AssetStatusRow): void {
    this.pending.set(row.assetId, row);
    if (this.coalesce) return;

    this.coalesce = setTimeout(() => {
      const updates = [...this.pending.values()];
      this.pending.clear();
      this.coalesce = null;
      this.statuses.update((current) => {
        const next = { ...current };
        for (const update of updates) next[update.assetId] = update;
        return next;
      });
    }, 40);
  }

  statusKey(status: AssetStatusRow | null): string {
    switch (status?.status) {
      case 'healthy':
        return 'dashboard.statusHealthy';
      case 'warning':
        return 'dashboard.statusWarning';
      case 'alert':
        return 'dashboard.statusAlert';
      case 'offline':
        return 'dashboard.statusOffline';
      default:
        return 'dashboard.statusUnknown';
    }
  }

  connectionKey(): string {
    const state: RealtimeState = this.connection();
    switch (state) {
      case 'live':
        return 'dashboard.live';
      case 'connecting':
        return 'dashboard.connecting';
      case 'stopped':
        // A refused session is paused honestly — never "reconnecting" forever.
        return 'dashboard.paused';
      default:
        return 'dashboard.reconnecting';
    }
  }

  /** Null is "no reading ever arrived" — never "now" (the UI must not invent). */
  lastSeen(at: string | null | undefined): string {
    if (!at) return this.transloco.translate('dashboard.neverSeen');

    const minutes = Math.max(0, Math.floor((this.now() - Date.parse(at)) / 60_000));
    if (minutes < 1) return this.transloco.translate('dashboard.lastSeenJustNow');
    if (minutes < 60) {
      return this.transloco.translate('dashboard.lastSeenMinutes', { minutes });
    }

    // A silent device must not read "seen 4320 min ago": roll the same truth up.
    const hours = Math.floor(minutes / 60);
    if (hours < 24) return this.transloco.translate('dashboard.lastSeenHours', { hours });

    return this.transloco.translate('dashboard.lastSeenDays', {
      days: Math.floor(hours / 24),
    });
  }

  absolute(at: string | null | undefined): string {
    if (!at) return this.transloco.translate('dashboard.neverSeen');
    return new Intl.DateTimeFormat(this.transloco.activeLang(), {
      dateStyle: 'medium',
      timeStyle: 'short',
    }).format(new Date(at));
  }

  private visible(): boolean {
    return document.visibilityState === 'visible';
  }
}
