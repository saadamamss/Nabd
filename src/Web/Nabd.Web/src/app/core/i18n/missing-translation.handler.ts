import { isDevMode, Injectable } from '@angular/core';
import { TranslocoMissingHandler, TranslocoMissingHandlerData } from '@jsverse/transloco';

/**
 * Spec 01, test 2: an absent key must fail loudly in tests/dev instead of
 * leaking English silently. A production build still renders the key itself —
 * visible, never invented text.
 */
@Injectable({ providedIn: 'root' })
export class ThrowingMissingHandler implements TranslocoMissingHandler {
  handle(key: string, data: TranslocoMissingHandlerData): string {
    if (isDevMode()) {
      throw new Error(`Missing translation key "${key}" in "${data.activeLang}"`);
    }
    return key;
  }
}
