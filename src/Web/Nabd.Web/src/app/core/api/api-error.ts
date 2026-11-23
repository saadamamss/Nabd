import { HttpErrorResponse } from '@angular/common/http';

/**
 * One map for every refusal the doors can answer (spec 02, A3). The server's
 * codes are constants; this table is the UI's translation layer, and an
 * unmapped code renders the raw code plus a generic line — never silence.
 */
const CODE_KEYS: Readonly<Record<string, string>> = {
  'identity:forbidden': 'errors.forbidden',
  'identity:cross_tenant_target': 'errors.notFound',
  'identity:user_not_found': 'errors.notFound',
  'identity:tenant_not_found': 'errors.notFound',
  'assets:asset_not_found': 'errors.notFound',
  'assets:device_not_found': 'errors.notFound',
  'assets:device_revoked': 'errors.deviceRevoked',
  'rules:rule_not_found': 'errors.notFound',
  'rules:device_not_found': 'errors.notFound',
  'identity:email_taken': 'errors.emailTaken',
  'identity:slug_taken': 'errors.slugTaken',
  'identity:last_admin': 'errors.lastAdmin',
  'identity:self_delete': 'errors.selfDelete',
  'identity:invalid_locale': 'errors.invalidLocale',
  'identity:role_unknown': 'errors.roleUnknown',
  'identity:invalid_email': 'errors.invalidEmail',
  'identity:weak_password': 'errors.weakPassword',
  'assets:invalid_name': 'errors.invalidName',
  'assets:unknown_template': 'errors.unknownTemplate',
  'rules:offline_refused': 'errors.offlineRefused',
  'rules:duplicate_rule': 'errors.duplicateRule',
  'rules:invalid_rule': 'errors.invalidRule',
  'rules:invalid_mute_window': 'errors.invalidMuteWindow',
  'rules:mute_window_overlap': 'errors.muteWindowOverlap',
};

/** Every code this UI knows how to translate (the parity test enumerates it). */
export const KNOWN_ERROR_CODES: readonly string[] = Object.keys(CODE_KEYS);

export interface ApiProblem {
  /** The server's code when it sent one; null for transport/shape failures. */
  code: string | null;
  /** A dictionary key — always present, so a screen never renders nothing. */
  key: string;
}

export function describeApiError(error: unknown): ApiProblem {
  const response = error instanceof HttpErrorResponse ? error : null;
  const body = response?.error as { code?: unknown } | null | undefined;
  const code = typeof body?.code === 'string' ? body.code : null;

  if (code && CODE_KEYS[code]) return { code, key: CODE_KEYS[code] };
  if (response?.status === 0) return { code, key: 'errors.network' };

  if (code) {
    // Honest fallback (A3): show the raw code, log it, never invent a meaning.
    console.warn(`Nabd UI: unmapped API code "${code}"`);
    return { code, key: 'errors.unexpected' };
  }

  return { code: null, key: 'errors.unexpected' };
}
