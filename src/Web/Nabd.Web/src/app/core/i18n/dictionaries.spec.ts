import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { DICTIONARIES } from '../../testing/test-dictionaries';

function leaves(value: unknown, prefix = ''): Map<string, unknown> {
  const out = new Map<string, unknown>();
  if (value === null || typeof value !== 'object') return out;

  for (const [key, child] of Object.entries(value as Record<string, unknown>)) {
    const path = prefix ? `${prefix}.${key}` : key;
    if (child !== null && typeof child === 'object') {
      for (const [leafPath, leaf] of leaves(child, path)) out.set(leafPath, leaf);
    } else {
      out.set(path, child);
    }
  }

  return out;
}

const SRC = join(process.cwd(), 'src');

describe('dictionaries', () => {
  it('ar and en expose the same recursive key set', () => {
    const en = [...leaves(DICTIONARIES['en']).keys()].sort();
    const ar = [...leaves(DICTIONARIES['ar']).keys()].sort();

    expect(ar).toEqual(en);
  });

  it('no dictionary value is empty', () => {
    for (const lang of ['en', 'ar'] as const) {
      for (const [key, value] of leaves(DICTIONARIES[lang])) {
        expect(value, `${lang}.${key} must be a non-empty string`).toEqual(
          expect.stringMatching(/\S/),
        );
      }
    }
  });

  it('no template under src carries Arabic literals', () => {
    const templates = (readdirSync(SRC, { recursive: true }) as string[]).filter((file) =>
      file.endsWith('.html'),
    );

    expect(templates.length).toBeGreaterThan(0);

    for (const file of templates) {
      const content = readFileSync(join(SRC, file), 'utf8');
      expect(content, `${file} must take its Arabic from the dictionary`).not.toMatch(
        /[\u0600-\u06FF]/,
      );
    }
  });
});
