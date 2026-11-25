import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { API_BASE } from './api.config';

/** The three resolutions the server chooses (C1). The client never picks one. */
export type HistorySource = 'raw' | 'hourly' | 'daily';

/** One bucket. `t` is the bucket start (UTC); `n` is the real reading count. */
export interface HistoryPoint {
  t: string;
  min: number;
  max: number;
  avg: number;
  n: number;
}

/**
 * `GET /api/v1/assets/{id}/history` (0029). `source`/`bucketSeconds` are present
 * even when `points` is empty — the label must not guess (REV 4 #1).
 */
export interface AssetHistoryResponse {
  source: HistorySource;
  bucketSeconds: number;
  channel: string;
  from: string;
  to: string;
  points: HistoryPoint[];
}

@Injectable({ providedIn: 'root' })
export class HistoryApi {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE);

  /** Both bounds are UTC instants on the wire (the server stores event time in UTC). */
  read(assetId: string, channel: string, from: Date, to: Date): Observable<AssetHistoryResponse> {
    const params = new HttpParams()
      .set('channel', channel)
      .set('from', from.toISOString())
      .set('to', to.toISOString());

    return this.http.get<AssetHistoryResponse>(
      `${this.base}/api/v1/assets/${assetId}/history`,
      { params },
    );
  }
}
