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
 * the toggle is a request to the server, not a private browser preference.
 */
@Injectable({ providedIn: 'root' })
export class LocaleService {
  private readonly transloco = inject(TranslocoService);
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly notice = inject(NoticeService);
  private readonly base = inject(API_BASE);

  private readonly current = signal<AppLocale>('en');
  readonly locale = this.current.asReadonly();

  /** Boot: the login snapshot's locale decides direction before any page data loads. */
  init(locale: string | null | undefined): void {
    this.use(normalizeLocale(locale));
  }

  use(locale: AppLocale): void {
    this.current.set(locale);
    this.transloco.setActiveLang(locale);

    const html = document.documentElement;
    html.setAttribute('lang', locale);
    html.setAttribute('dir', locale === 'ar' ? 'rtl' : 'ltr');
  }

  toggle(): void {
    const next: AppLocale = this.current() === 'ar' ? 'en' : 'ar';
    this.use(next);

    const user = this.auth.user();
    if (!user) return;

    this.auth.updateLocale(next);
    this.http
      .patch(`${this.base}/api/v1/tenants/${user.tenantId}/users/${user.userId}/locale`, {
        locale: next,
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
