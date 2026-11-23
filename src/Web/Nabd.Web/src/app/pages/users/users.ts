import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiProblem, describeApiError } from '../../core/api/api-error';
import { TenantAdminApi, UserRow } from '../../core/api/tenant-admin.service';
import { AuthService } from '../../core/auth/auth.service';

const ROLES = [
  { code: 'viewer', key: 'roles.viewer' },
  { code: 'tenant_admin', key: 'roles.tenantAdmin' },
] as const;

/**
 * People (spec 02): the server owns the laws — last admin, self delete, email
 * taken — so every refusal is rendered where it lands and the list is refetched
 * instead of hand-patched.
 */
@Component({
  selector: 'app-users-page',
  imports: [ReactiveFormsModule, TranslocoPipe],
  template: `
    <section class="ui-page">
      <header class="ui-head">
        <div>
          <span class="ui-kicker">{{ 'nav.settings' | transloco }}</span>
          <h1 class="ui-title">{{ 'users.title' | transloco }}</h1>
          <p class="ui-sub">{{ 'users.body' | transloco }}</p>
        </div>
        @if (canManage()) {
          <button class="ui-btn is-primary" type="button" (click)="toggleCreate()">
            {{ (creating() ? 'common.cancel' : 'users.add') | transloco }}
          </button>
        }
      </header>

      @if (error(); as problem) {
        <p class="ui-note is-error">
          {{ problem.key | transloco }}
          @if (problem.code) {
            <code>{{ problem.code }}</code>
          }
        </p>
      }

      @if (creating()) {
        <form class="ui-panel" [formGroup]="form" (ngSubmit)="create()">
          <h2 class="ui-title">{{ 'users.createTitle' | transloco }}</h2>
          <div class="ui-grid">
            <label class="ui-field">
              <span>{{ 'users.email' | transloco }}</span>
              <input type="email" formControlName="email" autocomplete="off" required />
            </label>
            <label class="ui-field">
              <span>{{ 'users.displayName' | transloco }}</span>
              <input type="text" formControlName="displayName" required />
            </label>
            <label class="ui-field">
              <span>{{ 'users.role' | transloco }}</span>
              <select formControlName="role">
                @for (role of roles; track role.code) {
                  <option [value]="role.code">{{ role.key | transloco }}</option>
                }
              </select>
            </label>
            <label class="ui-field">
              <span>{{ 'users.locale' | transloco }}</span>
              <select formControlName="locale">
                <option value="ar">{{ 'locale.ar' | transloco }}</option>
                <option value="en">{{ 'locale.en' | transloco }}</option>
              </select>
            </label>
            <label class="ui-field">
              <span>{{ 'users.password' | transloco }}</span>
              <input type="password" formControlName="password" autocomplete="new-password" required />
              <small>{{ 'users.passwordHint' | transloco }}</small>
            </label>
          </div>
          <div class="ui-actions">
            <button class="ui-btn is-primary" type="submit" [disabled]="form.invalid || creatingBusy()">
              {{ (creatingBusy() ? 'users.creating' : 'users.create') | transloco }}
            </button>
          </div>
        </form>
      }

      @if (removeTarget(); as target) {
        <div class="ui-overlay" role="presentation">
          <div class="ui-dialog" role="dialog" aria-modal="true" aria-labelledby="remove-person">
            <h2 id="remove-person">{{ 'users.deleteTitle' | transloco }}</h2>
            <p class="ui-note">
              {{ 'users.deleteBody' | transloco: { email: target.email } }}
            </p>
            <div class="ui-actions">
              <button class="ui-btn" type="button" (click)="removeTarget.set(null)">
                {{ 'common.cancel' | transloco }}
              </button>
              <button class="ui-btn is-danger" type="button" (click)="remove(target)" [disabled]="busy()">
                {{ 'users.remove' | transloco }}
              </button>
            </div>
          </div>
        </div>
      }

      @if (loading()) {
        <p class="ui-note">{{ 'common.loading' | transloco }}</p>
      } @else if (!rows().length) {
        <p class="ui-panel ui-empty">{{ 'users.empty' | transloco }}</p>
      } @else {
        <div class="ui-table-wrap">
          <table class="ui-table">
            <thead>
              <tr>
                <th scope="col">{{ 'users.displayName' | transloco }}</th>
                <th scope="col">{{ 'users.role' | transloco }}</th>
                <th scope="col">{{ 'users.locale' | transloco }}</th>
                @if (canManage()) {
                  <th scope="col">{{ 'common.actions' | transloco }}</th>
                }
              </tr>
            </thead>
            <tbody>
              @for (row of rows(); track row.userId) {
                <tr>
                  <td>
                    <strong>{{ row.displayName || row.email }}</strong>
                    @if (row.displayName && row.displayName !== row.email) {
                      <div class="ui-hint">{{ row.email }}</div>
                    }
                  </td>
                  <td>
                    @if (canManage()) {
                      <select
                        [value]="row.role"
                        [disabled]="busy()"
                        (change)="changeRole(row, $any($event.target).value)"
                        [attr.aria-label]="'users.role' | transloco"
                      >
                        @for (role of roles; track role.code) {
                          <option [value]="role.code">{{ role.key | transloco }}</option>
                        }
                      </select>
                    } @else {
                      <span class="ui-badge">{{ roleKey(row.role) | transloco }}</span>
                    }
                  </td>
                  <td>
                    <select
                      [value]="row.locale"
                      [disabled]="busy() || (!canManage() && row.userId !== me())"
                      (change)="changeLocale(row, $any($event.target).value)"
                      [attr.aria-label]="'users.locale' | transloco"
                    >
                      <option value="ar">{{ 'locale.ar' | transloco }}</option>
                      <option value="en">{{ 'locale.en' | transloco }}</option>
                    </select>
                  </td>
                  @if (canManage()) {
                    <td>
                      <button
                        class="ui-btn is-small is-danger"
                        type="button"
                        (click)="removeTarget.set(row)"
                        [disabled]="busy()"
                      >
                        {{ 'common.delete' | transloco }}
                      </button>
                    </td>
                  }
                </tr>
              }
            </tbody>
          </table>
        </div>
        <p class="ui-hint">{{ 'users.roleHint' | transloco }}</p>
      }
    </section>
  `,
})
export class UsersPage implements OnInit {
  private readonly api = inject(TenantAdminApi);
  private readonly auth = inject(AuthService);
  private readonly fb = inject(FormBuilder);

  readonly roles = ROLES;
  readonly rows = signal<UserRow[]>([]);
  readonly loading = signal(true);
  readonly error = signal<ApiProblem | null>(null);
  readonly creating = signal(false);
  readonly creatingBusy = signal(false);
  readonly busy = signal(false);
  readonly removeTarget = signal<UserRow | null>(null);

  readonly me = computed(() => this.auth.user()?.userId ?? '');
  readonly tenantId = computed(() => this.auth.user()?.tenantId ?? '');
  readonly canManage = computed(() => this.auth.user()?.role !== 'viewer');

  readonly form = this.fb.group({
    email: ['', [Validators.required, Validators.email]],
    displayName: ['', Validators.required],
    role: ['viewer'],
    locale: ['en'],
    password: ['', [Validators.required, Validators.minLength(8)]],
  });

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.api.users(this.tenantId()).subscribe({
      next: (rows) => {
        this.rows.set(rows);
        this.loading.set(false);
      },
      error: (error: unknown) => {
        this.error.set(describeApiError(error));
        this.rows.set([]);
        this.loading.set(false);
      },
    });
  }

  toggleCreate(): void {
    this.creating.update((open) => !open);
    this.error.set(null);
    if (this.creating()) this.form.reset({ role: 'viewer', locale: 'en' });
  }

  create(): void {
    if (this.form.invalid || this.creatingBusy()) return;
    this.creatingBusy.set(true);
    this.error.set(null);

    const value = this.form.getRawValue();
    this.api
      .createUser(this.tenantId(), {
        email: value.email ?? '',
        displayName: value.displayName ?? '',
        locale: value.locale ?? 'en',
        password: value.password ?? '',
        roleCode: value.role ?? 'viewer',
      })
      .subscribe({
        next: () => {
          this.creatingBusy.set(false);
          this.creating.set(false);
          this.form.reset({ role: 'viewer', locale: 'en' });
          this.load();
        },
        error: (error: unknown) => {
          this.creatingBusy.set(false);
          this.error.set(describeApiError(error));
        },
      });
  }

  changeRole(row: UserRow, role: string): void {
    if (role === row.role) return;
    this.busy.set(true);
    this.error.set(null);
    this.api.setRole(this.tenantId(), row.userId, role).subscribe({
      next: () => this.afterMutation(),
      error: (error: unknown) => this.failedMutation(error),
    });
  }

  changeLocale(row: UserRow, locale: string): void {
    if (locale === row.locale) return;
    this.busy.set(true);
    this.error.set(null);
    this.api.setLocale(this.tenantId(), row.userId, locale).subscribe({
      next: () => this.afterMutation(),
      error: (error: unknown) => this.failedMutation(error),
    });
  }

  remove(row: UserRow): void {
    this.busy.set(true);
    this.error.set(null);
    this.api.deleteUser(this.tenantId(), row.userId).subscribe({
      next: () => {
        this.removeTarget.set(null);
        this.afterMutation();
      },
      error: (error: unknown) => this.failedMutation(error),
    });
  }

  roleKey(role: string): string {
    return ROLES.find((entry) => entry.code === role)?.key ?? 'roles.member';
  }

  /** The server's answer is the truth: refetch instead of patching the array. */
  private afterMutation(): void {
    this.busy.set(false);
    this.load();
  }

  private failedMutation(error: unknown): void {
    this.busy.set(false);
    this.error.set(describeApiError(error));
    this.load();
  }
}
