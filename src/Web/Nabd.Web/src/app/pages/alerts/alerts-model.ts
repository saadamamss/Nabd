import { Params } from '@angular/router';
import { AlertDetail, AlertQuery } from '../../core/api/alerts.service';

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
    assetId: isGuid(assetId) ? assetId.toLowerCase() : '',
    limit: query.get('limit') === '500' ? 500 : 100,
  };
}

export function isGuid(value: string | null | undefined): value is string {
  return typeof value === 'string' && GUID.test(value);
}

/**
 * The dashboard links here with `?alertId=` (spec 03). The detail route is the
 * alert's real address, so a focused id is forwarded, never left as a dead
 * parameter the list silently ignores.
 */
export function focusedAlertId(query: QueryRead): string | null {
  const alertId = query.get('alertId');
  return isGuid(alertId) ? alertId.toLowerCase() : null;
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
 * One typed fact of the detail view. Numbers are marked so the template can keep
 * them LTR inside RTL chrome; nothing here is re-derived from the rule.
 */
export interface AlertFact {
  key: string;
  value: string;
  numeric?: boolean;
}

/**
 * The evidence as the alert stored it (`AlertDetail`, copied at crossing time).
 * The rule is never re-read: a later PATCH of thresholds must not rewrite what a
 * human saw at 3 AM (test fact 7). Offline alerts carry silence instead of a
 * reading, so absent facts are simply absent.
 */
export function evidenceFacts(
  detail: AlertDetail | null | undefined,
  lang: string,
): AlertFact[] {
  if (!detail) return [];

  const number = new Intl.NumberFormat(lang, { maximumFractionDigits: 2 });
  const when = new Intl.DateTimeFormat(lang, { dateStyle: 'medium', timeStyle: 'short' });
  const facts: AlertFact[] = [];

  if (detail.value != null) {
    facts.push({ key: 'alerts.evidenceValue', value: number.format(detail.value), numeric: true });
  }

  const low = detail.thresholdLow;
  const high = detail.thresholdHigh;
  if (low != null && high != null) {
    facts.push({
      key: 'alerts.evidenceThreshold',
      value: `${number.format(low)} – ${number.format(high)}`,
      numeric: true,
    });
  } else if (high != null) {
    facts.push({ key: 'alerts.evidenceThreshold', value: `> ${number.format(high)}`, numeric: true });
  } else if (low != null) {
    facts.push({ key: 'alerts.evidenceThreshold', value: `< ${number.format(low)}`, numeric: true });
  }

  if (detail.channelKey) {
    facts.push({ key: 'alerts.evidenceChannel', value: detail.channelKey, numeric: true });
  }

  if (detail.deviceId) {
    facts.push({ key: 'alerts.evidenceDevice', value: detail.deviceId, numeric: true });
  }

  if (detail.offlineGapMinutes != null) {
    facts.push({
      key: 'alerts.evidenceSilence',
      value: number.format(detail.offlineGapMinutes),
      numeric: true,
    });
  }

  if (detail.lastSeenAtUtc) {
    facts.push({ key: 'alerts.evidenceLastSeen', value: when.format(new Date(detail.lastSeenAtUtc)) });
  }

  return facts;
}

/**
 * P5 spec 05, S3b — the timeline's vocabulary, pure and testable.
 *
 * A stored reason is a **code**, not a sentence (R3): `condition_restored` is
 * engine vocabulary and would leak into both locales if printed verbatim, while
 * an admin's resolve text is the opposite — it must never be translated. So the
 * known codes get a dictionary key and everything else renders exactly as stored:
 * free text stays verbatim, and a future code can never render blank.
 */

/** Known lifecycle events get a dictionary key; an unknown event is shown as received. */
export function eventKey(event: string): string | null {
  switch (event) {
    case 'opened':
      return 'alerts.eventOpened';
    case 'reopened':
      return 'alerts.eventReopened';
    case 'acknowledged':
      return 'alerts.eventAcknowledged';
    case 'resolved':
      return 'alerts.eventResolved';
    case 'suppressed':
      return 'alerts.eventSuppressed';
    case 'deduplicated':
      return 'alerts.eventDeduplicated';
    default:
      return null;
  }
}

/** The engine's reason codes, in both locales; anything else is shown as stored. */
export function reasonKey(reason: string | null | undefined): string | null {
  switch (reason) {
    case 'mute_window':
      return 'alerts.reasonMuteWindow';
    case 'cooldown':
      return 'alerts.reasonCooldown';
    case 'condition_restored':
      return 'alerts.reasonConditionRestored';
    case 'device_online_restored':
      return 'alerts.reasonDeviceOnlineRestored';
    case 'manual':
      return 'alerts.reasonManual';
    default:
      return null;
  }
}

/**
 * P5 spec 05, S4 — the resolve reason, measured the way the server measures it.
 *
 * `AlertService.ResolveAsync` trims first and refuses only what is still longer
 * than 200 characters, then turns a blank into "manual". Mirroring the trim here
 * keeps the client from refusing text the server would accept (a pasted 200-char
 * sentence with a trailing newline) and keeps the counter honest.
 */
export const RESOLVE_REASON_MAX = 200;

/** What the dialog sends: the trimmed text, or nothing at all when it is blank. */
export function resolveReasonOf(reason: string): string | undefined {
  const why = reason.trim();
  return why.length ? why : undefined;
}

/** Counted after the same trim, so the counter and the refusal agree. */
export function reasonTooLong(reason: string): boolean {
  return reason.trim().length > RESOLVE_REASON_MAX;
}

/**
 * The actor's name from the users list (all roles can read it). `null` when the
 * id no longer resolves — the caller says "a user" instead of a blank cell.
 * A display name is the human answer; the email is the fallback that always exists.
 */
export function actorNameOf(
  users: readonly { userId: string; email: string; displayName?: string | null }[],
  actorUserId: string,
): string | null {
  const user = users.find((candidate) => candidate.userId === actorUserId);
  if (!user) return null;

  return user.displayName?.trim() || user.email;
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
