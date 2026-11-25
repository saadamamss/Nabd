import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { API_BASE } from './api.config';

/** The four states the server can return (P5 spec 03, B12 — never `muted`). */
export type AssetStatus = 'healthy' | 'warning' | 'alert' | 'offline';

/**
 * One row of `GET /api/v1/assets/status` (0028). No tenant id on the wire.
 *
 * The host serializes with `WhenWritingNull`, so a null fact is *absent* on the
 * wire, not `null`: the optional `?` is the honest type (`undefined` at runtime).
 * Treat both the same — the page only asks "is there a value?".
 */
export interface AssetStatusRow {
  assetId: string;
  status: AssetStatus;
  /** The winning alert's opened_at; absent exactly when the asset is healthy. */
  since?: string | null;
  alertId?: string | null;
  severity?: 'info' | 'warn' | 'critical' | null;
  /** Newest device_status.last_seen_at over the asset's devices; absent = never heard. */
  lastSeenAt?: string | null;
  isActive: boolean;
}

/** The `alerts` channel's hint — the page refreshes from doors, never from this. */
export interface AlertChangeHint {
  alertId: string;
  assetId: string;
  severity: string;
  status: string;
  since: string;
}

/**
 * The `alerts` channel's wire envelope (B10): `kind` discriminates the hint.
 * The server sends `{kind, alert: {...}}`; unwrapping it belongs to the consumer
 * (spec 05), not to the socket — the socket must not erase the discriminator.
 */
export type AlertChangeKind = 'raised' | 'changed';

export interface AlertChange {
  kind: AlertChangeKind;
  alert: AlertChangeHint;
}

/**
 * The live dashboard's snapshot reader (P5 spec 03). The tenant is the token's —
 * this client never sends one, because the door does not accept one.
 */
@Injectable({ providedIn: 'root' })
export class DashboardApi {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE);

  statuses(includeArchived = false): Observable<AssetStatusRow[]> {
    return this.http.get<AssetStatusRow[]>(`${this.base}/api/v1/assets/status`, {
      params: { includeArchived: String(includeArchived) },
    });
  }
}
