import { computed, inject, Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { catchError, finalize, map, Observable, of, shareReplay, tap, throwError } from 'rxjs';
import { API_BASE } from '../api/api.config';
import { AppLocale, LoginResponse, UserSnapshot } from './auth.models';

/** Every auth door is called with credentials so the cookie rides along. */
const CREDENTIALS = { withCredentials: true } as const;

/**
 * A failed `/logout` cannot delete an HttpOnly cookie from script. This local
 * flag (a boolean, never a token) keeps the next reload from resurrecting the
 * session, and makes boot finish the kill instead.
 */
const SIGNED_OUT_KEY = 'nabd.signed-out';

function signedOut(): boolean {
  try {
    return sessionStorage.getItem(SIGNED_OUT_KEY) === '1';
  } catch {
    return false;
  }
}

/**
 * Owns the session (spec 01, S7): the access token lives in memory only. The
 * rotating refresh token never enters JavaScript — the API sets it as an
 * HttpOnly cookie, so a reload can only restore the session by asking
 * `/auth/refresh` and letting the browser present it.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE);

  private readonly session = signal<LoginResponse | null>(null);
  private refreshInFlight$: Observable<void> | null = null;

  readonly user = computed<UserSnapshot | null>(() => this.session()?.user ?? null);
  readonly accessToken = computed(() => this.session()?.accessToken ?? null);
  readonly isAuthenticated = computed(() => this.accessToken() !== null);

  login(email: string, password: string): Observable<LoginResponse> {
    return this.http
      .post<LoginResponse>(
        `${this.base}/api/v1/auth/login`,
        { email, password },
        CREDENTIALS,
      )
      .pipe(tap((response) => this.apply(response)));
  }

  /**
   * The anonymous reset-request door (spec 07): it answers 204 for every address,
   * known or not, so the caller may not infer anything from success — only a
   * 429 (with its wait) or a transport failure is a different outcome.
   */
  requestReset(email: string): Observable<void> {
    return this.http.post<void>(`${this.base}/api/v1/auth/reset-password`, { email });
  }

  /**
   * The anonymous reset-confirm door: the token travels in the body, once.
   * Success revokes every refresh token server-side, so the caller must clear
   * any local session it still holds (the cookie is already dead).
   */
  confirmReset(token: string, newPassword: string): Observable<void> {
    return this.http.post<void>(`${this.base}/api/v1/auth/reset-confirm`, {
      token,
      newPassword,
    });
  }

  /**
   * The password-change door (spec 07, C11): self only, and the ids come from
   * the token's own snapshot — never from a form or a route. No snapshot means
   * no honest door to call, so nothing leaves the client.
   */
  changePassword(currentPassword: string, newPassword: string): Observable<void> {
    const user = this.user();
    if (!user) return throwError(() => new Error('changePassword needs a session snapshot'));

    return this.http.patch<void>(
      `${this.base}/api/v1/auth/${user.tenantId}/users/${user.userId}/password`,
      { currentPassword, newPassword },
    );
  }

  /** Stores a successful login/refresh pair; the cookie rotates server-side. */
  apply(response: LoginResponse): void {
    this.session.set(response);
  }

  /** The server's locale is the truth; keep the local snapshot in step. */
  updateLocale(locale: AppLocale): void {
    this.session.update((current) =>
      current ? { ...current, user: { ...current.user, locale } } : current,
    );
  }

  /**
   * One shared refresh for every caller: a burst of 401s must not mint a burst
   * of rotations (replay of an old token revokes every session).
   */
  refresh(): Observable<void> {
    if (this.refreshInFlight$) return this.refreshInFlight$;

    this.refreshInFlight$ = this.http
      .post<LoginResponse>(`${this.base}/api/v1/auth/refresh`, null, CREDENTIALS)
      .pipe(
        tap((response) => this.apply(response)),
        map(() => undefined as void),
        catchError((error: unknown) => {
          this.clearLocal();
          return throwError(() => error);
        }),
        finalize(() => {
          this.refreshInFlight$ = null;
        }),
        shareReplay({ bufferSize: 1, refCount: true }),
      );

    return this.refreshInFlight$;
  }

  /**
   * App initializer: the cookie is unreadable, so the only honest way to know
   * whether a session survived the reload is to ask. A 401 means signed out.
   */
  async restore(): Promise<void> {
    // The user signed out but the server never confirmed: clear the cookie now
    // and stay signed out, rather than restoring a session they ended.
    if (signedOut()) {
      await new Promise<void>((resolve) => {
        this.logout().subscribe({ complete: () => resolve(), error: () => resolve() });
      });
      return;
    }

    await new Promise<void>((resolve) => {
      this.refresh().subscribe({ next: () => resolve(), error: () => resolve() });
    });
  }

  /**
   * Clears local state first and tells the server to revoke the cookie's
   * session; a failing or unreachable `/logout` must never trap the user.
   */
  logout(): Observable<void> {
    this.clearLocal();
    this.setSignedOut(true);

    return this.http
      .post<void>(`${this.base}/api/v1/auth/logout`, null, CREDENTIALS)
      .pipe(
        tap(() => this.setSignedOut(false)),
        catchError(() => of(void 0)),
      );
  }

  private setSignedOut(value: boolean): void {
    try {
      if (value) sessionStorage.setItem(SIGNED_OUT_KEY, '1');
      else sessionStorage.removeItem(SIGNED_OUT_KEY);
    } catch {
      // Blocked storage: logout still clears everything this tab can clear.
    }
  }

  clearLocal(): void {
    this.session.set(null);
  }
}
