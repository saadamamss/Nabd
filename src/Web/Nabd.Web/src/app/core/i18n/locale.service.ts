import { inject, Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { TranslocoService } from '@jsverse/transloco';
import { catchError, of } from 'rxjs';
import { API_BASE } from '../api/api.config';
import { AuthService } from '../auth/auth.service';
import { AppLocale, normalizeLocale } from '../auth/auth.models';
import { NoticeService } from '../ui/notice.service';

/**
 * Owns `<html lang dir>` and the server-persisted preference (spec 01, S9):
 * signed in, the toggle is a request to the server, not a private preference.
 * Signed out there is no account to store it on, so the last choice is kept in
 * storage purely so `/login` can boot in it; the session snapshot still wins on
 * the next login, so the two never drift.
 */
const LOCALE_KEY = 'nabd.locale';

/** Set only when a signed-out visitor actually flips the toggle. */
const PENDING_KEY = 'nabd.locale.pending';

/** Only the two shipped locales are trusted; anything else boots in English. */
function readStoredLocale(): AppLocale | null {
  try {
    const value = localStorage.getItem(LOCALE_KEY);
    return value === 'ar' || value === 'en' ? value : null;
  } catch {
    return null;
  }
}
@Injectable({ providedIn: 'root' })
export class LocaleService {
  private readonly transloco = inject(TranslocoService);
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly notice = inject(NoticeService);
  private readonly base = inject(API_BASE);

  private readonly current = signal<AppLocale>('en');
  readonly locale = this.current.asReadonly();

  /**
   * Boot: the login snapshot's locale decides direction before any page data
   * loads; an anonymous reload falls back to the last choice made on this
   * browser, so the login page stops snapping back to English.
   */
  init(locale: string | null | undefined): void {
    this.use(normalizeLocale(locale ?? readStoredLocale()));
  }

  use(locale: AppLocale): void {
    this.current.set(locale);
    this.transloco.setActiveLang(locale);

    const html = document.documentElement;
    html.setAttribute('lang', locale);
    html.setAttribute('dir', locale === 'ar' ? 'rtl' : 'ltr');

    try {
      localStorage.setItem(LOCALE_KEY, locale);
    } catch {
      // Blocked storage: the language still applies for this view.
    }
  }

  toggle(): void {
    const next: AppLocale = this.current() === 'ar' ? 'en' : 'ar';
    this.use(next);

    if (!this.auth.user()) {
      // A deliberate choice at the front door; carry it through sign-in.
      try {
        sessionStorage.setItem(PENDING_KEY, '1');
      } catch {
        // Blocked storage: the language still applies to this view.
      }
      return;
    }

    this.persist(next);
  }

  /**
   * Sign-in handoff. If this tab explicitly chose a language before signing in,
   * that choice wins and is written to the account, so the server (not the
   * browser) remains the stored truth. With no such choice the account's own
   * locale boots the app, exactly as before.
   */
  adoptLoginLocale(accountLocale: string | null | undefined): void {
    let pending = false;
    try {
      pending = sessionStorage.getItem(PENDING_KEY) === '1';
      sessionStorage.removeItem(PENDING_KEY);
    } catch {
      // Blocked storage: fall back to the account's locale.
    }

    // No explicit choice, or the account already agrees: boot from the account
    // and skip a pointless PATCH.
    if (!pending || this.current() === normalizeLocale(accountLocale)) {
      this.init(accountLocale);
      return;
    }

    this.persist(this.current());
  }

  private persist(locale: AppLocale): void {
    const user = this.auth.user();
    if (!user) return;

    this.auth.updateLocale(locale);
    this.http
      .patch(`${this.base}/api/v1/tenants/${user.tenantId}/users/${user.userId}/locale`, {
        locale,
      })
      .pipe(
        catchError(() => {
          // Keep the language the user is reading in; the server may disagree.
          this.notice.show('locale.persistFailed');
          return of(void 0);
        }),
      )
      .subscribe();
  }
}
