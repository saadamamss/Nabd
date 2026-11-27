import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { API_BASE } from './api.config';

/**
 * P5 spec 08 (FR-38): the user's own Telegram channel, as the doors answer it.
 *
 * `verifiedAt` and `botUsername` are optional because the host omits nulls
 * (`WhenWritingNull`, spec 08 C2) — an unlinked state has no `verifiedAt`
 * member at all, and a ticket with no configured bot has no `botUsername`.
 */
export interface TelegramState {
  linked: boolean;
  verifiedAt?: string;
  severities: string[];
}

/** The one-time ticket: the plaintext code exists only in this reply (spec 08 N7). */
export interface TelegramTicket {
  code: string;
  expiresAt: string;
  botUsername?: string;
}

@Injectable({ providedIn: 'root' })
export class TelegramApi {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE);

  get(): Observable<TelegramState> {
    return this.http.get<TelegramState>(`${this.base}/api/v1/me/telegram`);
  }

  link(): Observable<TelegramTicket> {
    return this.http.post<TelegramTicket>(`${this.base}/api/v1/me/telegram/link`, null);
  }
}
