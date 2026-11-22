import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideRouter } from '@angular/router';
import {
  provideTransloco,
  provideTranslocoMissingHandler,
} from '@jsverse/transloco';
import { routes } from './app.routes';
import { authInterceptor } from './core/auth/auth.interceptor';
import { AuthService } from './core/auth/auth.service';
import { LocaleService } from './core/i18n/locale.service';
import { ThrowingMissingHandler } from './core/i18n/missing-translation.handler';
import { TranslocoHttpLoader } from './core/i18n/transloco-http.loader';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(withInterceptors([authInterceptor])),
    provideTransloco({
      config: {
        availableLangs: ['ar', 'en'],
        defaultLang: 'en',
        fallbackLang: 'en',
        reRenderOnLangChange: true,
      },
      loader: TranslocoHttpLoader,
    }),
    provideTranslocoMissingHandler(ThrowingMissingHandler),
    // A reload with a live refresh token restores the session (and its locale)
    // before the first guarded navigation resolves.
    provideAppInitializer(() => {
      const auth = inject(AuthService);
      const locale = inject(LocaleService);
      return auth.restore().then(() => locale.init(auth.user()?.locale));
    }),
  ],
};
