import { inject, Injectable, signal } from '@angular/core';
import { HubConnection, HubConnectionBuilder, HubConnectionState } from '@microsoft/signalr';
import { API_BASE } from '../api/api.config';
import { AlertChange, AssetStatusRow } from '../api/dashboard.service';
import { AuthService } from '../auth/auth.service';
import { REALTIME_CHANNELS } from './realtime.channels';

/** What the indicator may honestly say. */
export type RealtimeState = 'connecting' | 'live' | 'reconnecting' | 'stopped';

export interface DashboardSocketHandlers {
  onStatus: (row: AssetStatusRow) => void;
  /** The whole envelope, so the consumer sees `kind` (B10); never an unwrapped hint. */
  onAlert: (change: AlertChange) => void;
  /** Fired after a reconnect: the snapshot must be fetched again (B5). */
  onResync: () => void;
}

/**
 * Owns the `/hubs/dashboard` connection (P5 spec 03, B5/B6/B9).
 *
 * The token comes from the in-memory session through `accessTokenFactory`; the
 * server's tenant group is derived from that token's claims, so this class never
 * names a tenant. A dropped socket is a HINT lost, never a wrong screen: on
 * reconnect the page refetches the snapshot.
 *
 * Reconnect policy (the 2026-11-24 storm): a retry is never a fixed interval.
 * Attempts back off 1 → 2 → 4 → 8 → 16 → 30 s (capped, ±20 % jitter), a live
 * connection resets the ladder, and a refused token is answered once by a
 * session refresh instead of being hammered. Every callback carries the
 * generation it was created in, so a `stop()`ed connection's late `onclose`
 * cannot resurrect it or start a second retry chain.
 */
@Injectable({ providedIn: 'root' })
export class DashboardSocket {
  private readonly auth = inject(AuthService);
  private readonly base = inject(API_BASE);

  readonly state = signal<RealtimeState>('stopped');

  private connection: HubConnection | null = null;
  private handlers: DashboardSocketHandlers | null = null;
  private retry: ReturnType<typeof setTimeout> | null = null;
  /** Bumped by every open/stop: older callbacks are inert. */
  private generation = 0;
  /** Consecutive failed attempts — the backoff exponent. */
  private failures = 0;
  /** The refresh-once guard: one refresh per failure streak, never a refresh loop. */
  private refreshed = false;

  private static readonly BASE_DELAY_MS = 1_000;
  private static readonly MAX_DELAY_MS = 30_000;

  start(handlers: DashboardSocketHandlers): void {
    this.handlers = handlers;
    if (this.connection) return;

    // A fresh mount is a fresh streak: an old outage's backoff step and its
    // refresh-once guard must not follow the page across a destroy/create.
    this.failures = 0;
    this.refreshed = false;

    this.clearRetry();
    this.open();
  }

  stop(): void {
    this.handlers = null;
    this.generation += 1;          // every in-flight callback is now stale
    this.clearRetry();

    const connection = this.connection;
    this.connection = null;
    this.state.set('stopped');

    if (connection && connection.state !== HubConnectionState.Disconnected) {
      void connection.stop().catch(() => undefined);
    }
  }

  private open(): void {
    const handlers = this.handlers;
    if (!handlers) return;

    this.clearRetry();

    const generation = (this.generation += 1);
    const current = (): boolean => generation === this.generation;
    this.state.set('connecting');

    const connection = new HubConnectionBuilder()
      .withUrl(`${this.base}/hubs/dashboard`, {
        accessTokenFactory: () => this.auth.accessToken() ?? '',
      })
      .withAutomaticReconnect()
      .build();

    connection.on(REALTIME_CHANNELS.assetStatus, (row: AssetStatusRow) => {
      if (current()) handlers.onStatus(row);
    });
    // B10: pass the envelope through untouched — `kind` is part of the contract.
    connection.on(REALTIME_CHANNELS.alerts, (change: AlertChange) => {
      if (current()) handlers.onAlert(change);
    });

    connection.onreconnecting(() => {
      if (current()) this.state.set('reconnecting');
    });
    connection.onreconnected(() => {
      if (!current()) return;
      this.live();
      handlers.onResync();
    });
    connection.onclose(() => {
      // Automatic reconnect gave up (or the server refused the socket).
      if (current()) this.retryLater();
    });

    this.connection = connection;
    connection
      .start()
      .then(() => {
        if (current()) this.live();
      })
      .catch((error: unknown) => {
        if (current()) this.failed(error);
      });
  }

  private live(): void {
    this.failures = 0;
    this.refreshed = false;
    this.state.set('live');
  }

  /**
   * A failed attempt. Two kinds only: a token the server refused (refresh once,
   * then give up honestly) and everything else (back off).
   */
  private failed(error: unknown): void {
    if (this.isAuthRefusal(error)) {
      if (this.refreshed) {
        // The session cannot be renewed: retrying would be a lie and a storm.
        this.halt();
        return;
      }

      this.refreshed = true;
      this.dropConnection();
      this.auth.refresh().subscribe({
        next: () => this.open(),
        error: () => this.halt(),
      });
      return;
    }

    this.retryLater();
  }

  private retryLater(): void {
    if (this.retry) return;

    if (this.failures === 0) {
      console.warn('Nabd realtime: dashboard hub connection failed; backing off');
    }

    const delay = this.nextDelay();
    this.state.set('reconnecting');

    this.retry = setTimeout(() => {
      this.retry = null;
      this.dropConnection();
      this.open();
    }, delay);
  }

  /** Exponential, capped, jittered — a network engineer's backoff, not a poll. */
  private nextDelay(): number {
    const raw = DashboardSocket.BASE_DELAY_MS * 2 ** this.failures;
    const capped = Math.min(raw, DashboardSocket.MAX_DELAY_MS);
    this.failures += 1;

    const jitter = 0.8 + Math.random() * 0.4;
    return Math.round(capped * jitter);
  }

  private halt(): void {
    console.warn('Nabd realtime: dashboard hub refused the session; live updates paused');
    this.generation += 1;
    this.clearRetry();
    this.dropConnection();
    this.state.set('stopped');
  }

  private dropConnection(): void {
    // Bumping here — not just in open() — makes the dropped connection's own
    // onclose inert at once: a dropped socket must never re-arm the chain.
    this.generation += 1;

    const connection = this.connection;
    this.connection = null;

    if (connection && connection.state !== HubConnectionState.Disconnected) {
      void connection.stop().catch(() => undefined);
    }
  }

  private clearRetry(): void {
    if (!this.retry) return;

    clearTimeout(this.retry);
    this.retry = null;
  }

  /** SignalR's negotiate failure for an expired/refused token (HttpError). */
  private isAuthRefusal(error: unknown): boolean {
    if (!error || typeof error !== 'object') return false;

    const status = (error as { statusCode?: unknown }).statusCode;
    if (status === 401 || status === 403) return true;

    const message = (error as { message?: unknown }).message;
    return typeof message === 'string' && /unauthori[sz]ed|forbidden/i.test(message);
  }
}
