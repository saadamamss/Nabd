import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { API_BASE } from './api.config';

/**
 * P5 spec 06 (F2/F7): the submit door's wire shape. There is deliberately no
 * tenant/user/author field — the server stamps those from the token and the
 * directory, and an extra member would be ignored anyway.
 */
export interface FeedbackRequest {
  message: string;
  /** The router pathname (never the query string): `/alerts`, not `/alerts?x=1`. */
  page: string;
  locale: string;
  contactEmail: string | null;
}

/** The 201 receipt: the door echoes nothing else back (no PII, F9). */
export interface FeedbackReceipt {
  id: string;
  createdAt: string;
}

@Injectable({ providedIn: 'root' })
export class FeedbackApi {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE);

  send(body: FeedbackRequest): Observable<FeedbackReceipt> {
    return this.http.post<FeedbackReceipt>(`${this.base}/api/v1/feedback`, body);
  }
}
