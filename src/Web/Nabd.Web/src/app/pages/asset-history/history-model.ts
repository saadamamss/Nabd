import type { HistoryPoint, HistorySource } from '../../core/api/history.service';

/**
 * P5 spec 04's pure bits — no Angular, no ECharts, so the facts are cheap to pin:
 * the chip window, the resolution label key, and the dense bucket grid whose
 * holes are `null` (a gap must break the line, C7).
 */

export type RangeKey = '1h' | '24h' | '7d' | '30d' | 'custom';

export const HOUR_MS = 3_600_000;

const RANGE_HOURS: Record<Exclude<RangeKey, 'custom'>, number> = {
  '1h': 1,
  '24h': 24,
  '7d': 24 * 7,
  '30d': 24 * 30,
};

/** The chips are instant arithmetic; "now" is the browser's clock, the server trims. */
export function rangeWindow(
  key: Exclude<RangeKey, 'custom'>,
  now: Date,
): { from: Date; to: Date } {
  return { from: new Date(now.getTime() - RANGE_HOURS[key] * HOUR_MS), to: new Date(now.getTime()) };
}

/**
 * The resolution label is a display fact, not a guess: raw carries its real
 * width (e.g. 58 s), the aggregates are named by their own grain.
 */
export function resolutionKey(source: HistorySource): string {
  switch (source) {
    case 'raw':
      return 'history.resolutionSeconds';
    case 'hourly':
      return 'history.resolutionHourly';
    case 'daily':
      return 'history.resolutionDaily';
  }
}

export interface HistorySeries {
  /** The server's bucket grid, ISO instants. */
  readonly axis: string[];
  readonly min: (number | null)[];
  /** `max - min`, stacked on top of `min` to paint the band. */
  readonly span: (number | null)[];
  readonly avg: (number | null)[];
}

/**
 * Densifies the server's sparse buckets onto its own grid. A missing bucket is
 * `null` in every series — never 0, never interpolated (C7). `bucketSeconds` is
 * the server's answered width, so the grid is exactly the grid it used.
 */
export function buildSeries(
  points: readonly HistoryPoint[],
  bucketSeconds: number,
): HistorySeries {
  const axis: string[] = [];
  const min: (number | null)[] = [];
  const span: (number | null)[] = [];
  const avg: (number | null)[] = [];

  if (!points.length || bucketSeconds <= 0) return { axis, min, span, avg };

  const byTime = new Map(points.map((point) => [Date.parse(point.t), point]));
  const width = bucketSeconds * 1000;
  const start = Date.parse(points[0].t);
  const end = Date.parse(points[points.length - 1].t);

  for (let at = start; at <= end; at += width) {
    const point = byTime.get(at);
    axis.push(new Date(at).toISOString());

    if (!point) {
      min.push(null);
      span.push(null);
      avg.push(null);
      continue;
    }

    min.push(point.min);
    span.push(point.max - point.min);
    avg.push(point.avg);
  }

  return { axis, min, span, avg };
}
