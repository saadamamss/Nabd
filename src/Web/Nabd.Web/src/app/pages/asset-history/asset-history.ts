import {
  Component,
  computed,
  effect,
  ElementRef,
  inject,
  OnDestroy,
  OnInit,
  signal,
  viewChild,
} from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { forkJoin } from 'rxjs';
import { LineChart } from 'echarts/charts';
import {
  AriaComponent,
  DataZoomComponent,
  GridComponent,
  LegendComponent,
  TooltipComponent,
} from 'echarts/components';
import * as echarts from 'echarts/core';
import type { EChartsCoreOption, EChartsType } from 'echarts/core';
import { CanvasRenderer } from 'echarts/renderers';
import { ApiProblem, describeApiError } from '../../core/api/api-error';
import {
  AssetHistoryResponse,
  HistoryApi,
  HistorySource,
} from '../../core/api/history.service';
import { AssetRow, TemplateChannel, TenantAdminApi } from '../../core/api/tenant-admin.service';
import { SelectComponent } from '../../core/ui/select';
import {
  buildSeries,
  HOUR_MS,
  rangeWindow,
  resolutionKey,
  type HistorySeries,
  type RangeKey,
} from './history-model';

// Only the pieces this page draws: a line chart, axes, tooltip, legend, the
// aria description and the canvas renderer. The whole library stays out of the
// initial bundle because the ROUTE is lazy (C9).
echarts.use([
  LineChart,
  AriaComponent,
  DataZoomComponent,
  GridComponent,
  LegendComponent,
  TooltipComponent,
  CanvasRenderer,
]);

/**
 * P5 spec 04 — one asset's history (FR-41/FR-43).
 *
 * The server decides the resolution and returns `source`/`bucketSeconds` even on
 * an empty range; this page only renders what it was answered. Gaps are nulls,
 * never zeros; the chart is lazy because the library is large (C5/C7/C9).
 */
@Component({
  selector: 'app-asset-history-page',
  imports: [RouterLink, SelectComponent, TranslocoPipe],
  template: `
    <section class="ui-page">
      <header class="ui-head">
        <div>
          <span class="ui-kicker">{{ 'nav.assets' | transloco }}</span>
          <h1 class="ui-title">{{ asset()?.name ?? ('history.title' | transloco) }}</h1>
          <p class="ui-sub">{{ 'history.body' | transloco }}</p>
        </div>
        <a class="ui-btn is-small" [routerLink]="['/assets']">
          {{ 'history.back' | transloco }}
        </a>
      </header>

      @if (problem(); as p) {
        <p class="ui-note is-error">
          {{ p.key | transloco }}
          @if (p.code) {
            <code>{{ p.code }}</code>
          }
        </p>
      }

      @if (loadingAsset()) {
        <p class="ui-note">{{ 'common.loading' | transloco }}</p>
      } @else if (!asset()) {
        <p class="ui-panel ui-empty">{{ 'errors.notFound' | transloco }}</p>
      } @else if (!channels().length) {
        <p class="ui-panel ui-empty">{{ 'history.noNumeric' | transloco }}</p>
      } @else {
        <div class="hist-controls">
          <label class="ui-field">
            <span>{{ 'history.channel' | transloco }}</span>
            <app-select
              [options]="channelOptions()"
              [value]="channel()"
              [ariaLabel]="'history.channel' | transloco"
              (valueChange)="selectChannel($event)"
            />
          </label>

          <div class="ui-field">
            <span>{{ 'history.range' | transloco }}</span>
            <div class="hist-chip-row" role="group" [attr.aria-label]="'history.range' | transloco">
              @for (chip of chips; track chip.key) {
                <button
                  class="ui-btn is-small"
                  type="button"
                  [class.is-primary]="range() === chip.key"
                  [attr.aria-pressed]="range() === chip.key"
                  (click)="chooseRange(chip.key)"
                >
                  {{ chip.labelKey | transloco }}
                </button>
              }
            </div>
          </div>

          @if (range() === 'custom') {
            <label class="ui-field">
              <span>{{ 'history.from' | transloco }}</span>
              <input
                type="datetime-local"
                [value]="customFrom()"
                (change)="customFrom.set(inputValue($event))"
              />
            </label>
            <label class="ui-field">
              <span>{{ 'history.to' | transloco }}</span>
              <input
                type="datetime-local"
                [value]="customTo()"
                (change)="customTo.set(inputValue($event))"
              />
            </label>
            <button class="ui-btn is-primary is-small" type="button" (click)="load()">
              {{ 'history.apply' | transloco }}
            </button>
          }
        </div>

        @if (response(); as data) {
          <p class="hist-meta">
            <span class="ui-badge is-muted">
              {{ resolutionLabel(data.source, data.bucketSeconds) }}
            </span>
            <span class="ui-hint">
              {{ 'history.points' | transloco: { count: data.points.length } }}
            </span>
            <span class="ui-hint">{{ selectedChannelLabel() }}</span>
          </p>
        }

        <div class="hist-chart-wrap">
          <div
            #chart
            class="hist-chart"
            role="img"
            [attr.aria-label]="'history.chartTitle' | transloco"
          ></div>
          @if (!loading() && !hasPoints()) {
            <p class="hist-empty-overlay">{{ 'history.empty' | transloco }}</p>
          }
        </div>

        @if (loading()) {
          <p class="ui-note">{{ 'history.loading' | transloco }}</p>
        } @else if (hasPoints()) {
          <div class="hist-table ui-table-wrap">
            <table class="ui-table">
              <thead>
                <tr>
                  <th scope="col">{{ 'history.tableTime' | transloco }}</th>
                  <th scope="col">{{ 'history.tableMin' | transloco }}</th>
                  <th scope="col">{{ 'history.tableMax' | transloco }}</th>
                  <th scope="col">{{ 'history.tableAvg' | transloco }}</th>
                  <th scope="col">{{ 'history.tableCount' | transloco }}</th>
                </tr>
              </thead>
              <tbody>
                @for (row of tableRows(); track row.t) {
                  <tr>
                    <td>{{ absolute(row.t) }}</td>
                    <td>{{ number(row.min) }}</td>
                    <td>{{ number(row.max) }}</td>
                    <td>{{ number(row.avg) }}</td>
                    <td>{{ row.n }}</td>
                  </tr>
                }
              </tbody>
            </table>
            @if (tableTruncated()) {
              <p class="ui-hint">
                {{ 'history.tableNote' | transloco: { shown: tableRows().length, total: response()?.points?.length ?? 0 } }}
              </p>
            }
          </div>
        }
      }
    </section>
  `,
})
export class AssetHistoryPage implements OnInit, OnDestroy {
  private readonly route = inject(ActivatedRoute);
  private readonly admin = inject(TenantAdminApi);
  private readonly history = inject(HistoryApi);
  private readonly transloco = inject(TranslocoService);

  readonly assetId = this.route.snapshot.paramMap.get('assetId') ?? '';

  readonly asset = signal<AssetRow | null>(null);
  readonly channels = signal<readonly TemplateChannel[]>([]);
  readonly channel = signal('');
  readonly range = signal<RangeKey>('24h');
  readonly customFrom = signal('');
  readonly customTo = signal('');
  readonly loadingAsset = signal(true);
  readonly loading = signal(false);
  readonly problem = signal<ApiProblem | null>(null);
  readonly response = signal<AssetHistoryResponse | null>(null);

  readonly chips = [
    { key: '1h', labelKey: 'history.range1h' },
    { key: '24h', labelKey: 'history.range24h' },
    { key: '7d', labelKey: 'history.range7d' },
    { key: '30d', labelKey: 'history.range30d' },
    { key: 'custom', labelKey: 'history.rangeCustom' },
  ] as const satisfies readonly { key: RangeKey; labelKey: string }[];

  private readonly chartHost = viewChild<ElementRef<HTMLDivElement>>('chart');
  private chart: EChartsType | null = null;
  private observer: ResizeObserver | null = null;

  readonly hasPoints = computed(() => (this.response()?.points.length ?? 0) > 0);

  readonly selectedChannel = computed(() =>
    this.channels().find((entry) => entry.key === this.channel()) ?? null,
  );

  readonly channelOptions = computed(() => {
    const arabic = this.transloco.activeLang() === 'ar';
    return this.channels().map((entry) => ({
      value: entry.key,
      label: `${arabic ? entry.nameAr : entry.nameEn}${entry.unit ? ` · ${entry.unit}` : ''}`,
    }));
  });

  readonly series = computed<HistorySeries | null>(() => {
    const data = this.response();
    return data ? buildSeries(data.points, data.bucketSeconds) : null;
  });

  readonly tableRows = computed(() => {
    const points = this.response()?.points ?? [];
    return points.slice(Math.max(0, points.length - 100));
  });

  readonly tableTruncated = computed(() => (this.response()?.points.length ?? 0) > 100);

  constructor() {
    // The chart can only be initialized once its container exists: the page
    // starts in its loading branch, so `viewChild` becomes non-null later.
    // An effect on the signal is the honest trigger (no afterRender race).
    effect(() => {
      const host = this.chartHost()?.nativeElement;
      if (!host) return;

      if (this.chart && this.chart.getDom() !== host) {
        this.observer?.disconnect();
        this.chart.dispose();
        this.chart = null;
      }

      if (!this.chart) {
        this.chart = echarts.init(host, null, { renderer: 'canvas' });
        if (typeof ResizeObserver !== 'undefined') {
          this.observer = new ResizeObserver(() => this.chart?.resize());
          this.observer.observe(host);
        }
      }

      // Dependencies: a new answer, or a language switch (labels + tooltip).
      this.response();
      this.transloco.activeLang();
      this.renderChart();
    });
  }

  ngOnInit(): void {
    forkJoin({ assets: this.admin.assets(true), templates: this.admin.templates() }).subscribe({
      next: ({ assets, templates }) => {
        const asset = assets.find((entry) => entry.assetId === this.assetId) ?? null;
        this.asset.set(asset);

        const template = templates.find((entry) => entry.templateId === asset?.templateId) ?? null;
        const numeric = template?.channels.filter((entry) => entry.dataType === 'number') ?? [];
        this.channels.set(numeric);
        this.channel.set(numeric[0]?.key ?? '');
        this.loadingAsset.set(false);
        if (numeric.length) this.load();
      },
      error: (error: unknown) => {
        this.problem.set(describeApiError(error));
        this.loadingAsset.set(false);
      },
    });
  }

  ngOnDestroy(): void {
    this.observer?.disconnect();
    this.chart?.dispose();
  }

  selectChannel(key: string): void {
    if (!key || key === this.channel()) return;
    this.channel.set(key);
    this.load();
  }

  chooseRange(key: RangeKey): void {
    this.range.set(key);

    if (key === 'custom') {
      if (!this.customFrom() || !this.customTo()) {
        const local = (date: Date): string =>
          new Date(date.getTime() - date.getTimezoneOffset() * 60_000)
            .toISOString()
            .slice(0, 16);
        const now = new Date();
        this.customFrom.set(local(new Date(now.getTime() - 24 * HOUR_MS)));
        this.customTo.set(local(now));
      }
      return;
    }

    this.load();
  }

  load(): void {
    const channel = this.channel();
    const window = this.window();
    if (!channel) return;

    if (!window) {
      this.problem.set({ code: 'history:invalid_range', key: 'errors.historyRange' });
      return;
    }

    this.loading.set(true);
    this.problem.set(null);

    this.history.read(this.assetId, channel, window.from, window.to).subscribe({
      next: (data) => {
        this.response.set(data);
        this.loading.set(false);
      },
      error: (error: unknown) => {
        this.response.set(null);
        this.problem.set(describeApiError(error));
        this.loading.set(false);
      },
    });
  }

  resolutionLabel(source: HistorySource, bucketSeconds: number): string {
    return this.transloco.translate(resolutionKey(source), { seconds: bucketSeconds });
  }

  selectedChannelLabel(): string {
    const entry = this.selectedChannel();
    if (!entry) return '';
    const name = this.transloco.activeLang() === 'ar' ? entry.nameAr : entry.nameEn;
    return entry.unit ? `${name} · ${entry.unit}` : name;
  }

  absolute(iso: string): string {
    return new Intl.DateTimeFormat(this.transloco.activeLang(), {
      dateStyle: 'medium',
      timeStyle: 'short',
    }).format(new Date(iso));
  }

  number(value: number): string {
    return new Intl.NumberFormat(this.transloco.activeLang(), {
      maximumFractionDigits: 2,
    }).format(value);
  }

  inputValue(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  private window(): { from: Date; to: Date } | null {
    const key = this.range();
    if (key !== 'custom') return rangeWindow(key, new Date());

    const from = this.customFrom() ? new Date(this.customFrom()) : null;
    const to = this.customTo() ? new Date(this.customTo()) : null;
    if (!from || !to || Number.isNaN(from.getTime()) || Number.isNaN(to.getTime())) return null;

    return { from, to };
  }

  private renderChart(): void {
    if (!this.chart) return;

    const data = this.response();
    if (!data || !data.points.length) {
      this.chart.clear();
      return;
    }

    this.chart.setOption(this.optionFor(data), { notMerge: true });
  }

  private optionFor(data: AssetHistoryResponse): EChartsCoreOption {
    const series = buildSeries(data.points, data.bucketSeconds);
    const lang = this.transloco.activeLang();
    const average = this.transloco.translate('history.avg');
    const band = this.transloco.translate('history.band');
    const empty = this.transloco.translate('history.empty');
    const count = this.transloco.translate('history.count');
    const unit = this.selectedChannel()?.unit ?? '';

    const axisLabel = new Intl.DateTimeFormat(
      lang,
      data.bucketSeconds >= 86_400
        ? { dateStyle: 'medium' }
        : { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' },
    );
    const numbers = new Intl.NumberFormat(lang, { maximumFractionDigits: 2 });
    const withUnit = (value: number): string => `${numbers.format(value)}${unit ? ` ${unit}` : ''}`;
    const byTime = new Map(data.points.map((point) => [Date.parse(point.t), point]));

    return {
      animation: false,
      textStyle: { fontFamily: 'inherit' },
      aria: { enabled: true },
      legend: { top: 0, selectedMode: false, data: [average, band] },
      grid: { top: 36, bottom: 8, left: 8, right: 12, containLabel: true },
      tooltip: {
        trigger: 'axis',
        formatter: (params: unknown) => {
          const first = Array.isArray(params) ? (params[0] as { dataIndex?: number }) : undefined;
          const index = first?.dataIndex;
          if (index === undefined) return '';

          const iso = series.axis[index];
          const point = byTime.get(Date.parse(iso));
          if (!point) return `<strong>${axisLabel.format(new Date(iso))}</strong><br/>${empty}`;

          return [
            `<strong>${axisLabel.format(new Date(point.t))}</strong>`,
            `${average}: ${withUnit(point.avg)}`,
            `${band}: ${withUnit(point.min)} – ${withUnit(point.max)}`,
            `${count}: ${point.n}`,
          ].join('<br/>');
        },
      },
      xAxis: {
        type: 'category',
        boundaryGap: false,
        data: series.axis.map((iso) => axisLabel.format(new Date(iso))),
        axisLabel: { hideOverlap: true, fontSize: 11 },
      },
      yAxis: {
        type: 'value',
        scale: true,
        splitLine: { lineStyle: { opacity: 0.25 } },
        axisLabel: { formatter: (value: number) => withUnit(value) },
      },
      series: [
        {
          name: band,
          type: 'line',
          stack: 'band',
          data: series.min,
          symbol: 'none',
          lineStyle: { opacity: 0 },
          areaStyle: { opacity: 0 },
          silent: true,
          connectNulls: false,
        },
        {
          name: band,
          type: 'line',
          stack: 'band',
          data: series.span,
          symbol: 'none',
          lineStyle: { opacity: 0 },
          areaStyle: { opacity: 0.14 },
          silent: true,
          connectNulls: false,
        },
        {
          name: average,
          type: 'line',
          data: series.avg,
          symbol: 'none',
          lineStyle: { width: 2 },
          connectNulls: false,
        },
      ],
    };
  }
}
