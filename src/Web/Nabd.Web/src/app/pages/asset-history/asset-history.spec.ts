import { HttpErrorResponse } from '@angular/common/http';
import { describeApiError } from '../../core/api/api-error';
import { routes } from '../../app.routes';
import { DICTIONARIES } from '../../testing/test-dictionaries';
import { buildSeries, rangeWindow, resolutionKey } from './history-model';

// P5 spec 04 (4b) — the page's facts, RED first.
//
// The chart itself is not rendered here (jsdom has no canvas): the net pins the
// three things the page can lie about — the window, the resolution label, and a
// gap becoming a null instead of a zero — plus the two 422 codes and the lazy
// route that keeps ECharts out of the initial bundle.

const point = (t: string, min: number, max: number, avg: number, n: number) => ({
  t,
  min,
  max,
  avg,
  n,
});

describe('history model', () => {
  it('the range chips are exact instant windows', () => {
    const now = new Date('2026-11-25T12:00:00Z');

    expect(rangeWindow('1h', now).from.toISOString()).toBe('2026-11-25T11:00:00.000Z');
    expect(rangeWindow('24h', now).from.toISOString()).toBe('2026-11-24T12:00:00.000Z');
    expect(rangeWindow('7d', now).from.toISOString()).toBe('2026-11-18T12:00:00.000Z');
    expect(rangeWindow('30d', now).from.toISOString()).toBe('2026-10-26T12:00:00.000Z');
    expect(rangeWindow('7d', now).to.toISOString()).toBe(now.toISOString());
  });

  it('a missing bucket is null in every series, never a zero', () => {
    const series = buildSeries(
      [
        point('2026-11-18T10:00:00Z', 4, 6, 5, 2),
        point('2026-11-18T12:00:00Z', 7, 7, 7, 1),
      ],
      3600,
    );

    expect(series.axis).toEqual([
      '2026-11-18T10:00:00.000Z',
      '2026-11-18T11:00:00.000Z',
      '2026-11-18T12:00:00.000Z',
    ]);
    expect(series.avg).toEqual([5, null, 7]);
    expect(series.min).toEqual([4, null, 7]);
    expect(series.span).toEqual([2, null, 0]);
  });

  it('the band is max-min, so the stacked pair paints exactly the envelope', () => {
    const series = buildSeries([point('2026-11-18T10:00:00Z', 2, 8, 5, 10)], 3600);

    expect(series.min).toEqual([2]);
    expect(series.span).toEqual([6]);
  });

  it('an empty range has no grid at all', () => {
    expect(buildSeries([], 58)).toEqual({ axis: [], min: [], span: [], avg: [] });
  });

  it('the resolution key names the server fact', () => {
    expect(resolutionKey('raw')).toBe('history.resolutionSeconds');
    expect(resolutionKey('hourly')).toBe('history.resolutionHourly');
    expect(resolutionKey('daily')).toBe('history.resolutionDaily');
  });
});

describe('history dictionaries and errors', () => {
  const keys = [
    'history.title',
    'history.empty',
    'history.noNumeric',
    'history.rangeCustom',
    'history.resolutionSeconds',
    'history.resolutionHourly',
    'history.resolutionDaily',
    'history.tableNote',
    'assets.openHistory',
    'errors.historyRange',
    'errors.historyChannel',
  ];

  it.each(keys)('%s exists in both dictionaries', (key) => {
    for (const lang of ['en', 'ar'] as const) {
      const leaf = key.split('.').reduce<unknown>(
        (value, part) =>
          value && typeof value === 'object'
            ? (value as Record<string, unknown>)[part]
            : undefined,
        DICTIONARIES[lang],
      );

      expect(typeof leaf, `${lang}.${key} must be a string`).toBe('string');
      expect((leaf as string).trim().length, `${lang}.${key} must not be blank`).toBeGreaterThan(0);
    }
  });

  it('both history 422 codes map to a dictionary key', () => {
    const range = describeApiError(
      new HttpErrorResponse({ error: { code: 'history:invalid_range' }, status: 422 }),
    );
    expect(range.key).toBe('errors.historyRange');

    const channel = describeApiError(
      new HttpErrorResponse({ error: { code: 'history:unknown_channel' }, status: 422 }),
    );
    expect(channel.key).toBe('errors.historyChannel');
  });
});

describe('the history route is lazy', () => {
  it('loads the page through loadComponent, never a static import', () => {
    const shell = routes.find((route) => route.path === '');
    const child = shell?.children?.find((route) => route.path === 'assets/:assetId/history');

    expect(child, 'the history route must exist').toBeDefined();
    expect(typeof child?.loadComponent).toBe('function');
    expect(child?.component).toBeUndefined();
  });
});
