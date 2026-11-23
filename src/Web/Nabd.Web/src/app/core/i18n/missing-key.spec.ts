import { Component, Injectable } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  provideTransloco,
  TRANSLOCO_MISSING_HANDLER,
  TranslocoMissingHandler,
  TranslocoPipe,
  TranslocoService,
} from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { provideTestTransloco } from '../../testing/test-transloco';
import { TestTranslocoLoader } from '../../testing/test-transloco.loader';

@Component({
  imports: [TranslocoPipe],
  template: `{{ key | transloco }}`,
})
class MissingKeyHost {
  key = 'definitely.not.a.key';
}

/** Records the keys the render path asks for; never leaks English. */
@Injectable()
class RecordingHandler implements TranslocoMissingHandler {
  static seen: string[] = [];

  handle(key: string): string {
    RecordingHandler.seen.push(key);
    return `[missing:${key}]`;
  }
}

describe('missing translation key', () => {
  it('the configured handler throws when the pipe asks for an absent key', async () => {
    TestBed.configureTestingModule({ providers: [...provideTestTransloco()] });
    const transloco = TestBed.inject(TranslocoService);
    await firstValueFrom(transloco.load('en'));

    // This is the exact call `TranslocoPipe` makes for every rendered key.
    expect(() => transloco.translate('definitely.not.a.key')).toThrow(
      /Missing translation key/,
    );
  });

  it('the render path consults the handler instead of falling back to English', async () => {
    RecordingHandler.seen = [];

    await TestBed.configureTestingModule({
      imports: [MissingKeyHost],
      providers: [
        provideTransloco({
          config: { availableLangs: ['ar', 'en'], defaultLang: 'en', fallbackLang: 'en' },
          loader: TestTranslocoLoader,
        }),
        { provide: TRANSLOCO_MISSING_HANDLER, useValue: new RecordingHandler() },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(MissingKeyHost);
    fixture.detectChanges();
    await fixture.whenStable();

    expect(RecordingHandler.seen).toContain('definitely.not.a.key');
    // The rendered text is the handler's answer, not a silent identity fallback.
    expect((fixture.nativeElement as HTMLElement).textContent?.trim()).toBe(
      '[missing:definitely.not.a.key]',
    );
  });
});
