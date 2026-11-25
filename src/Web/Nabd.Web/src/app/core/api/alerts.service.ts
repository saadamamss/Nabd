import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
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
}
