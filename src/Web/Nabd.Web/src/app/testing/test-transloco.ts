import { EnvironmentProviders, Provider } from '@angular/core';
import {
  provideTransloco,
  provideTranslocoMissingHandler,
} from '@jsverse/transloco';
import { ThrowingMissingHandler } from '../core/i18n/missing-translation.handler';
import { TestTranslocoLoader } from './test-transloco.loader';

/** The app's composition, minus HTTP/proxy: real dictionaries, real handler. */
export function provideTestTransloco(): (Provider | EnvironmentProviders)[] {
  return [
    provideTransloco({
      config: {
        availableLangs: ['ar', 'en'],
        defaultLang: 'en',
        fallbackLang: 'en',
        reRenderOnLangChange: true,
      },
      loader: TestTranslocoLoader,
    }),
    provideTranslocoMissingHandler(ThrowingMissingHandler),
  ];
}
