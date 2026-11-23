import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { API_BASE } from './api.config';

/** The doors' wire shapes (camelCase on the wire), verified against the controllers. */

export interface UserRow {
  userId: string;
  tenantId: string;
  email: string;
  role: string;
  locale: string;
  displayName?: string | null;
}

export interface NewUser {
  email: string;
  displayName: string;
  locale: string;
  password: string;
  roleCode: string;
}

export interface AssetRow {
  assetId: string;
  tenantId: string;
  templateId: string;
  name: string;
  location: string | null;
  notes: string | null;
  isActive: boolean;
}

export interface TemplateChannel {
  key: string;
  nameAr: string;
  nameEn: string;
  unit: string;
  dataType: string;
  lowerLimit: number | null;
  upperLimit: number | null;
}

export interface TemplateRow {
  templateId: string;
  key: string;
  nameAr: string;
  nameEn: string;
  channels: TemplateChannel[];
}

export interface DeviceRow {
  deviceId: string;
  tenantId: string;
  assetId: string;
  templateId: string;
  name: string;
  isActive: boolean;
  tokenRevokedAtUtc: string | null;
}

export interface DeviceMint {
  device: DeviceRow;
  /** The only moment this value exists on a wire — shown once, never refetched. */
  token: string;
}

export interface MuteWindow {
  dayOfWeek: number;
  startMinute: number;
  endMinute: number;
}

export interface RuleRow {
  ruleId: string;
  tenantId: string;
  deviceId: string;
  channelKey: string | null;
  ruleType: string;
  thresholdLow: number | null;
  thresholdHigh: number | null;
  durationSeconds: number;
  cooldownMinutes: number;
  severity: string;
  enabled: boolean;
  muteWindows?: MuteWindow[] | null;
  offlineAfterMinutes?: number;
}

export interface RuleDraft {
  channelKey: string | null;
  ruleType: string;
  thresholdLow: number | null;
  thresholdHigh: number | null;
  durationSeconds: number;
  cooldownMinutes: number;
  severity: string;
  enabled?: boolean;
  muteWindows?: MuteWindow[];
  offlineAfterMinutes?: number;
}

/**
 * One client for the tenant-admin doors (spec 02). Every list is the server's
 * list; nothing is patched in place after a mutation — the page refetches.
 */
@Injectable({ providedIn: 'root' })
export class TenantAdminApi {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE);

  private url(path: string): string {
    return `${this.base}${path}`;
  }

  // ---- people ----
  users(tenantId: string): Observable<UserRow[]> {
    return this.http.get<UserRow[]>(this.url(`/api/v1/tenants/${tenantId}/users`));
  }

  createUser(tenantId: string, body: NewUser): Observable<UserRow> {
    return this.http.post<UserRow>(this.url(`/api/v1/tenants/${tenantId}/users`), body);
  }

  setRole(tenantId: string, userId: string, role: string): Observable<UserRow> {
    return this.http.patch<UserRow>(this.url(`/api/v1/tenants/${tenantId}/users/${userId}/roles`), {
      role,
    });
  }

  setLocale(tenantId: string, userId: string, locale: string): Observable<UserRow> {
    return this.http.patch<UserRow>(this.url(`/api/v1/tenants/${tenantId}/users/${userId}/locale`), {
      locale,
    });
  }

  deleteUser(tenantId: string, userId: string): Observable<void> {
    return this.http.delete<void>(this.url(`/api/v1/tenants/${tenantId}/users/${userId}`));
  }

  // ---- templates + assets ----
  templates(): Observable<TemplateRow[]> {
    return this.http.get<TemplateRow[]>(this.url('/api/v1/templates'));
  }

  assets(includeArchived = false): Observable<AssetRow[]> {
    return this.http.get<AssetRow[]>(this.url('/api/v1/assets'), {
      params: { includeArchived: String(includeArchived) },
    });
  }

  createAsset(body: {
    templateId: string;
    name: string;
    location: string | null;
    notes: string | null;
  }): Observable<AssetRow> {
    return this.http.post<AssetRow>(this.url('/api/v1/assets'), body);
  }

  updateAsset(
    assetId: string,
    body: { name?: string; location?: string | null; notes?: string | null },
  ): Observable<AssetRow> {
    return this.http.patch<AssetRow>(this.url(`/api/v1/assets/${assetId}`), body);
  }

  archiveAsset(assetId: string): Observable<void> {
    return this.http.delete<void>(this.url(`/api/v1/assets/${assetId}`));
  }

  // ---- devices ----
  devices(assetId: string): Observable<DeviceRow[]> {
    return this.http.get<DeviceRow[]>(this.url(`/api/v1/assets/${assetId}/devices`));
  }

  mintDevice(assetId: string, name: string): Observable<DeviceMint> {
    return this.http.post<DeviceMint>(this.url(`/api/v1/assets/${assetId}/devices`), { name });
  }

  reissueDevice(deviceId: string): Observable<DeviceMint> {
    return this.http.post<DeviceMint>(this.url(`/api/v1/devices/${deviceId}/token/reissue`), null);
  }

  revokeDevice(deviceId: string): Observable<void> {
    return this.http.delete<void>(this.url(`/api/v1/devices/${deviceId}`));
  }

  // ---- rules ----
  rules(deviceId: string): Observable<RuleRow[]> {
    return this.http.get<RuleRow[]>(this.url(`/api/v1/devices/${deviceId}/rules`));
  }

  createRule(deviceId: string, body: RuleDraft): Observable<RuleRow> {
    return this.http.post<RuleRow>(this.url(`/api/v1/devices/${deviceId}/rules`), {
      channelKey: body.channelKey,
      ruleType: body.ruleType,
      thresholdLow: body.thresholdLow,
      thresholdHigh: body.thresholdHigh,
      durationSeconds: body.durationSeconds,
      cooldownMinutes: body.cooldownMinutes,
      severity: body.severity,
    });
  }

  updateRule(deviceId: string, ruleId: string, body: RulePatch): Observable<RuleRow> {
    return this.http.patch<RuleRow>(
      this.url(`/api/v1/devices/${deviceId}/rules/${ruleId}`),
      body,
    );
  }

  deleteRule(deviceId: string, ruleId: string): Observable<void> {
    return this.http.delete<void>(this.url(`/api/v1/devices/${deviceId}/rules/${ruleId}`));
  }
}

export interface RulePatch {
  thresholdLow?: number | null;
  thresholdHigh?: number | null;
  durationSeconds?: number;
  cooldownMinutes?: number;
  severity?: string;
  enabled?: boolean;
  muteWindows?: MuteWindow[];
  offlineAfterMinutes?: number;
}
