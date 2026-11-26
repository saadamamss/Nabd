import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { map, Observable } from 'rxjs';
import { API_BASE } from './api.config';

/** What the list door accepts. Nothing here is ever `all` (spec 05 S2 fact 1). */
export interface AlertQuery {
  status?: string;
  severity?: string;
  assetId?: string;
  limit?: number;
}

/** The evidence of one crossing, typed as the Alerting room serializes it. */
export interface AlertDetail {
  value?: number | null;
  channelKey?: string | null;
  ruleType: string;
  thresholdLow?: number | null;
  thresholdHigh?: number | null;
  deviceId: string;
  offlineGapMinutes?: number | null;
  lastSeenAtUtc?: string | null;
}

/**
 * One row of `GET /api/v1/alerts` (`AlertSnapshot`). The host writes with
 * `WhenWritingNull`, so a null fact is *absent*, not `null` — hence the `?`.
 */
export interface AlertRow {
  alertId: string;
  tenantId: string;
  assetId: string;
  ruleId: string;
  type: string;
  severity: string;
  status: string;
  openedAt: string;
  resolvedAt?: string | null;
  acknowledgedAt?: string | null;
  acknowledgedBy?: string | null;
  titleEn: string;
  titleAr: string;
  detail: AlertDetail;
  /** P5 spec 05 (E9): the row fact — true when the episode opened muted. */
  quietHoursSuppressed?: boolean;
}

/**
 * One audit row of `GET /api/v1/alerts/{id}/events` (spec 05, E7). The host
 * writes with `WhenWritingNull`, so an absent actor, reason or detail is missing,
 * not `null`. `reason` is a code when the engine wrote it and free text when an
 * admin resolved; the UI decides how to say each.
 */
export interface AlertEvent {
  event: string;
  at: string;
  actorUserId?: string | null;
  reason?: string | null;
  detail?: unknown;
}

/**
 * The alert list door (FR-42). Filters are optional by construction: an absent
 * key is never sent, so `?status=all` cannot exist on the wire.
 */
@Injectable({ providedIn: 'root' })
export class AlertsApi {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE);

  list(query: AlertQuery = {}): Observable<AlertRow[]> {
    let params = new HttpParams();
    if (query.status) params = params.set('status', query.status);
    if (query.severity) params = params.set('severity', query.severity);
    if (query.assetId) params = params.set('assetId', query.assetId);
    if (query.limit) params = params.set('limit', String(query.limit));

    return this.http.get<AlertRow[]>(`${this.base}/api/v1/alerts`, { params });
  }

  /** One alert, with the evidence it stored when it opened (`GET /{id}`). */
  get(alertId: string): Observable<AlertRow> {
    return this.http.get<AlertRow>(`${this.base}/api/v1/alerts/${alertId}`);
  }

  /**
   * Acknowledge (`POST /{id}/ack`). Every role may do this — the first responder
   * is often not an admin (E2). The door answers the row it wrote, so the caller
   * never guesses the new status or the timestamp.
   */
  acknowledge(alertId: string): Observable<AlertRow> {
    return this.http.post<AlertRow>(`${this.base}/api/v1/alerts/${alertId}/ack`, null);
  }

  /**
   * Resolve (`POST /{id}/resolve?reason=`), tenant_admin only. A blank reason is
   * an **absent** parameter, never `?reason=`: the server records "manual" itself,
   * and the wire must not carry an empty string the UI invented (E3).
   */
  resolve(alertId: string, reason: string): Observable<AlertRow> {
    let params = new HttpParams();
    const why = reason.trim();
    if (why) params = params.set('reason', why);

    return this.http.post<AlertRow>(`${this.base}/api/v1/alerts/${alertId}/resolve`, null, {
      params,
    });
  }

  /**
   * The audit trail (`GET /{id}/events`, E7), ascending in the door's documented
   * order — the client renders what it receives and never re-sorts a history.
   * Lifecycle events by default; `includeEvaluation` adds suppressed/deduplicated.
   */
  events(alertId: string, includeEvaluation = false): Observable<AlertEvent[]> {
    let params = new HttpParams();
    if (includeEvaluation) params = params.set('includeEvaluation', 'true');

    return this.http
      .get<{ items: AlertEvent[] }>(`${this.base}/api/v1/alerts/${alertId}/events`, { params })
      .pipe(map((response) => response.items ?? []));
  }
}
