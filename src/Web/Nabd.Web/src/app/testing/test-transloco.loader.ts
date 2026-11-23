import { Injectable } from '@angular/core';
import { Translation, TranslocoLoader } from '@jsverse/transloco';
import { Observable, of } from 'rxjs';
import { DICTIONARIES } from './test-dictionaries';

@Injectable()
export class TestTranslocoLoader implements TranslocoLoader {
  getTranslation(lang: string): Observable<Translation> {
    return of(DICTIONARIES[lang === 'ar' ? 'ar' : 'en']);
  }
}
