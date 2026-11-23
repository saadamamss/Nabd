import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { Translation } from '@jsverse/transloco';

export type Dictionary = Record<string, unknown>;

/**
 * Tests read the shipped dictionaries, not a fixture: parity and missing-key
 * facts must describe what the app actually serves. Vitest runs with the
 * workspace root as cwd (`src/Web/Nabd.Web`).
 */
export function loadDictionary(lang: 'en' | 'ar'): Translation {
  return JSON.parse(
    readFileSync(join(process.cwd(), 'public', 'i18n', `${lang}.json`), 'utf8'),
  ) as Translation;
}

export const DICTIONARIES: Record<'en' | 'ar', Translation> = {
  en: loadDictionary('en'),
  ar: loadDictionary('ar'),
};
