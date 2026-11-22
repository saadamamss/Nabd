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
}

/** Mirrors `LoginResult` — refresh tokens rotate on every use. */
export interface LoginResponse {
  user: UserSnapshot;
  accessToken: string;
  refreshToken: string;
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
