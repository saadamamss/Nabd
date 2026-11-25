import { Params } from '@angular/router';
import { AlertQuery } from '../../core/api/alerts.service';

/**
 * P5 spec 05, S2 — the alert list's filter vocabulary, pure and testable.
 *
 * The UI's `all` is a *display* word, never a server word: `doorQuery` turns it
 * into an absent parameter, so the door is asked exactly what the user chose and
 * nothing else (test fact 1). The URL is the filter's only home (E4), so the same
 * two functions read the query string back and write it out again.
 */

export type AlertStatusFilter = 'all' | 'open' | 'acknowledged' | 'resolved';
export type AlertSeverityFilter = 'all' | 'info' | 'warn' | 'critical';
/** The door's cap is 500 and its default 100 (E5); the page offers the same two. */
export type AlertLimit = 100 | 500;

export interface AlertFilters {
  status: AlertStatusFilter;
  severity: AlertSeverityFilter;
  assetId: string;
  limit: AlertLimit;
}

export const DEFAULT_FILTERS: AlertFilters = {
  status: 'all',
  severity: 'all',
  assetId: '',
  limit: 100,
};

export const STATUS_FILTERS: readonly AlertStatusFilter[] = [
  'all',
  'open',
  'acknowledged',
  'resolved',
];

export const SEVERITY_FILTERS: readonly AlertSeverityFilter[] = [
  'all',
  'info',
  'warn',
  'critical',
];

export const LIMITS: readonly AlertLimit[] = [100, 500];

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Anything with the two getters Angular's `ParamMap` has. */
export type QueryRead = { get(name: string): string | null };

function isStatus(value: string | null): value is AlertStatusFilter {
  return value !== null && (STATUS_FILTERS as readonly string[]).includes(value);
}

function isSeverity(value: string | null): value is AlertSeverityFilter {
  return value !== null && (SEVERITY_FILTERS as readonly string[]).includes(value);
}

/**
 * Reads the URL. A hand-edited or stale value (an unknown status, a broken asset
 * id) falls back to its default instead of reaching the door — the server stays
 * the authority on what exists, and the client stops inventing filters.
 */
export function readFilters(query: QueryRead): AlertFilters {
  const status = query.get('status');
  const severity = query.get('severity');
  const assetId = query.get('assetId');

  return {
    status: isStatus(status) ? status : 'all',
    severity: isSeverity(severity) ? severity : 'all',
    assetId: assetId && GUID.test(assetId) ? assetId.toLowerCase() : '',
    limit: query.get('limit') === '500' ? 500 : 100,
  };
}

/** The URL form: defaults are absent, so a plain `/alerts` is a clean address. */
export function queryParams(filters: AlertFilters): Params {
  const params: Params = {};
  if (filters.status !== 'all') params['status'] = filters.status;
  if (filters.severity !== 'all') params['severity'] = filters.severity;
  if (filters.assetId) params['assetId'] = filters.assetId;
  if (filters.limit !== DEFAULT_FILTERS.limit) params['limit'] = String(filters.limit);
  return params;
}

/** The door's form: `all` is an absent parameter, never the literal string. */
export function doorQuery(filters: AlertFilters): AlertQuery {
  return {
    status: filters.status === 'all' ? undefined : filters.status,
    severity: filters.severity === 'all' ? undefined : filters.severity,
    assetId: filters.assetId || undefined,
    limit: filters.limit,
  };
}

/** A limit is not a filter: it changes the cap, not what matches. */
export function hasFilters(filters: AlertFilters): boolean {
  return filters.status !== 'all' || filters.severity !== 'all' || filters.assetId !== '';
}

/** Known statuses get a dictionary key; anything else is shown as received. */
export function statusKey(status: string): string | null {
  switch (status) {
    case 'open':
      return 'alerts.statusOpen';
    case 'acknowledged':
      return 'alerts.statusAcknowledged';
    case 'resolved':
      return 'alerts.statusResolved';
    default:
      return null;
  }
}

export function severityKey(severity: string): string | null {
  switch (severity) {
    case 'info':
      return 'alerts.severityInfo';
    case 'warn':
      return 'alerts.severityWarn';
    case 'critical':
      return 'alerts.severityCritical';
    default:
      return null;
  }
}

/** The server ships both titles; the locale picks one, it never re-translates. */
export function titleFor(alert: { titleEn: string; titleAr: string }, lang: string): string {
  return lang === 'ar' ? alert.titleAr : alert.titleEn;
}

/**
 * `AlertSnapshot` carries `AssetId` only (no invented server field), so the name
 * comes from the already-loaded assets list. A missing row is said out loud.
 */
export function assetNameOf(
  assets: readonly { assetId: string; name: string }[],
  assetId: string,
): string | null {
  return assets.find((asset) => asset.assetId === assetId)?.name ?? null;
}
