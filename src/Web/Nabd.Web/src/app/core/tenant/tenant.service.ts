import { inject, Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { catchError, map, Observable, of } from 'rxjs';
import { API_BASE } from '../api/api.config';
import { TenantSnapshot } from '../auth/auth.models';

/** Reads the tenant's display name for the topbar (spec 01, test 13). */
@Injectable({ providedIn: 'root' })
export class TenantService {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE);

  private readonly nameState = signal<string | null>(null);
  readonly name = this.nameState.asReadonly();

  load(tenantId: string): Observable<string | null> {
    return this.http.get<TenantSnapshot>(`${this.base}/api/v1/tenants/${tenantId}`).pipe(
      map((tenant) => tenant.name),
      map((name) => {
        this.nameState.set(name);
        return name;
      }),
      catchError(() => {
        // Degrade to the localized product name; never a blank topbar.
        this.nameState.set(null);
        return of(null);
      }),
    );
  }
}
