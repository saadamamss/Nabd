import { InjectionToken } from '@angular/core';

/**
 * Base for every API call. Empty by default: the dev proxy and production both
 * serve the API on the same origin (spec 01, S3).
 */
export const API_BASE = new InjectionToken<string>('nabd.apiBase', {
  providedIn: 'root',
  factory: () => '',
});
