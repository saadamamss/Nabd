import { computed, inject, Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { catchError, finalize, map, Observable, of, shareReplay, tap, throwError } from 'rxjs';
import { API_BASE } from '../api/api.config';
import { AppLocale, LoginResponse, UserSnapshot } from './auth.models';

const REFRESH_STORAGE_KEY = 'nabd.refresh';

/**
 * Owns the session (spec 01, S7): the access token lives in memory only, the
 * refresh token in `sessionStorage` and is **replaced on every rotation**.
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
      .post<LoginResponse>(`${this.base}/api/v1/auth/login`, { email, password })
      .pipe(tap((response) => this.apply(response)));
  }

  /** Stores a successful login/refresh pair (the refresh token always rotates). */
  apply(response: LoginResponse): void {
    this.session.set(response);
    sessionStorage.setItem(REFRESH_STORAGE_KEY, response.refreshToken);
  }

  refreshToken(): string | null {
    return sessionStorage.getItem(REFRESH_STORAGE_KEY);
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

    const token = this.refreshToken();
    if (!token) return throwError(() => new Error('no-refresh-token'));

    this.refreshInFlight$ = this.http
      .post<LoginResponse>(`${this.base}/api/v1/auth/refresh`, { refreshToken: token })
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

  /** App initializer: a reload with a live refresh token restores the session silently. */
  async restore(): Promise<void> {
    if (!this.refreshToken()) return;
    await new Promise<void>((resolve) => {
      this.refresh().subscribe({ next: () => resolve(), error: () => resolve() });
    });
  }

  /**
   * Posts `{refreshToken}` (the door is anonymous, spec 01) but clears local
   * state first: a failing or unreachable `/logout` must never trap the user.
   */
  logout(): Observable<void> {
    const refreshToken = this.refreshToken();
    this.clearLocal();
    if (!refreshToken) return of(void 0);

    return this.http
      .post<void>(`${this.base}/api/v1/auth/logout`, { refreshToken })
      .pipe(catchError(() => of(void 0)));
  }

  clearLocal(): void {
    this.session.set(null);
    sessionStorage.removeItem(REFRESH_STORAGE_KEY);
  }
}
