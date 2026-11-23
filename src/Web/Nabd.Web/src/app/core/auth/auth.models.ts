export type AppLocale = 'ar' | 'en';

export const APP_LOCALES: readonly AppLocale[] = ['ar', 'en'];
export const DEFAULT_LOCALE: AppLocale = 'en';
export const PLATFORM_ADMIN = 'platform_admin';

/** Mirrors `UserSnapshot` in Identity.Contracts (camelCase on the wire). */
export interface UserSnapshot {
  userId: string;
  tenantId: string;
  email: string;
  role: string;
  locale: string;
  /** Added in P5/02; older rows or a null name fall back to the email. */
  displayName?: string | null;
}

/**
 * Mirrors the API's `SessionResponse`: user + access token only. The rotating
 * refresh token is an HttpOnly cookie the browser attaches to `/auth/refresh`
 * and `/auth/logout`; it is deliberately unreadable from script.
 */
export interface LoginResponse {
  user: UserSnapshot;
  accessToken: string;
}

/** Mirrors `TenantSnapshot`. */
export interface TenantSnapshot {
  tenantId: string;
  name: string;
  isActive: boolean;
}

export function normalizeLocale(value: string | null | undefined): AppLocale {
  return value === 'ar' ? 'ar' : DEFAULT_LOCALE;
}
