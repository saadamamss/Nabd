import { HttpErrorResponse } from '@angular/common/http';
import { DICTIONARIES } from '../../testing/test-dictionaries';
import { describeApiError, KNOWN_ERROR_CODES } from './api-error';

function leafExists(lang: 'ar' | 'en', path: string): boolean {
  const walk = (value: unknown, parts: string[]): boolean => {
    if (!parts.length) return typeof value === 'string' && value.trim().length > 0;
    if (value === null || typeof value !== 'object') return false;
    const [head, ...rest] = parts;
    return walk((value as Record<string, unknown>)[head], rest);
  };
  return walk(DICTIONARIES[lang], path.split('.'));
}

function apiError(code: string, status = 409): HttpErrorResponse {
  return new HttpErrorResponse({ error: { code }, status, statusText: 'Conflict' });
}

/**
 * The spec-02 facts table, plus the two door codes the screens can meet today
 * (`invalid_email` from user create, `weak_password` from spec 07's split).
 * Hardcoded ON PURPOSE: a list read from the map would only prove the map
 * agrees with itself.
 */
const SERVER_CODES = [
  'identity:forbidden',
  'identity:cross_tenant_target',
  'identity:user_not_found',
  'identity:tenant_not_found',
  'assets:asset_not_found',
  'assets:device_not_found',
  'assets:device_revoked',
  'rules:rule_not_found',
  'rules:device_not_found',
  'identity:email_taken',
  'identity:slug_taken',
  'identity:last_admin',
  'identity:self_delete',
  'assets:unknown_template',
  'rules:offline_refused',
  'rules:duplicate_rule',
  'rules:invalid_rule',
  'rules:invalid_mute_window',
  'rules:mute_window_overlap',
  'identity:invalid_locale',
  'assets:invalid_name',
  'identity:role_unknown',
  'identity:invalid_email',
  'identity:weak_password',
] as const;

describe('describeApiError', () => {
  it('the map covers every code in the spec 02 table (the list is the contract, not the map)', () => {
    for (const code of SERVER_CODES) {
      expect(KNOWN_ERROR_CODES, `${code} must be mapped`).toContain(code);
    }
  });

  it('every known server code resolves to a non-empty string in both dictionaries', () => {
    expect(KNOWN_ERROR_CODES.length).toBeGreaterThan(10);

    for (const code of KNOWN_ERROR_CODES) {
      const key = describeApiError(apiError(code)).key;
      expect(key, `${code} must map to a key`).toMatch(/^errors\./);
      expect(leafExists('en', key), `en.${key} for ${code}`).toBe(true);
      expect(leafExists('ar', key), `ar.${key} for ${code}`).toBe(true);
    }
  });

  it('an unmapped code keeps the raw code and falls back to the generic line', () => {
    const problem = describeApiError(apiError('identity:brand_new_refusal'));
    expect(problem).toEqual({ code: 'identity:brand_new_refusal', key: 'errors.unexpected' });
  });

  it('a transport failure has no code and says the server was unreachable', () => {
    const problem = describeApiError(new HttpErrorResponse({ status: 0 }));
    expect(problem.key).toBe('errors.network');
    expect(problem.code).toBeNull();
  });
});
