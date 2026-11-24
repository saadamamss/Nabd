import { Component, computed, ElementRef, inject, OnInit, signal, viewChild } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiProblem, describeApiError } from '../../core/api/api-error';
import { TenantAdminApi, UserRow } from '../../core/api/tenant-admin.service';
import { AuthService } from '../../core/auth/auth.service';
import { NoticeService } from '../../core/ui/notice.service';
import { SelectComponent, type SelectOption } from '../../core/ui/select';

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
  imports: [NgTemplateOutlet, ReactiveFormsModule, SelectComponent, TranslocoPipe],
  template: `
    <section class="ui-page">
      <header class="ui-head">
        <div>
          <span class="ui-kicker">{{ 'nav.settings' | transloco }}</span>
          <h1 class="ui-title">{{ 'users.title' | transloco }}</h1>
          <p class="ui-sub">{{ 'users.body' | transloco }}</p>
        </div>
        @if (canManage()) {
          <button class="ui-btn is-primary" type="button" (click)="openCreate()">
            {{ 'users.add' | transloco }}
          </button>
        }
      </header>

      <!-- one error surface, rendered where the user is looking: inside the open
           dialog, otherwise above the list -->
      <ng-template #problem>
        @if (error(); as problem) {
          <p class="ui-note is-error">
            {{ problem.key | transloco }}
            @if (problem.code) {
              <code>{{ problem.code }}</code>
            }
          </p>
        }
      </ng-template>

      @if (!creating() && !removeTarget()) {
        <ng-container *ngTemplateOutlet="problem" />
      }

      @if (creating()) {
        <div class="ui-overlay" role="presentation" (keydown.escape)="closeCreate()">
          <div class="ui-dialog" role="dialog" aria-modal="true" aria-labelledby="create-person">
            <h2 id="create-person">{{ 'users.createTitle' | transloco }}</h2>
            <ng-container *ngTemplateOutlet="problem" />
            <form [formGroup]="form" (ngSubmit)="create()">
              <div class="ui-grid">
                <label class="ui-field">
                  <span>{{ 'users.email' | transloco }}</span>
                  <input #createEmail type="email" formControlName="email" autocomplete="off" required />
                </label>
                <label class="ui-field">
                  <span>{{ 'users.displayName' | transloco }}</span>
                  <input type="text" formControlName="displayName" required />
                </label>
                <div class="ui-field">
                  <span>{{ 'users.role' | transloco }}</span>
                  <app-select
                    formControlName="role"
                    [options]="roleOptions"
                    [ariaLabel]="'users.role' | transloco"
                  />
                </div>
                <div class="ui-field">
                  <span>{{ 'users.locale' | transloco }}</span>
                  <app-select
                    formControlName="locale"
                    [options]="localeOptions"
                    [ariaLabel]="'users.locale' | transloco"
                  />
                </div>
                <label class="ui-field">
                  <span>{{ 'users.password' | transloco }}</span>
                  <input type="password" formControlName="password" autocomplete="new-password" required />
                  <small>{{ 'users.passwordHint' | transloco }}</small>
                </label>
              </div>
              <div class="ui-actions">
                <button class="ui-btn" type="button" (click)="closeCreate()" [disabled]="creatingBusy()">
                  {{ 'common.cancel' | transloco }}
                </button>
                <button class="ui-btn is-primary" type="submit" [disabled]="form.invalid || creatingBusy()">
                  {{ (creatingBusy() ? 'users.creating' : 'users.create') | transloco }}
                </button>
              </div>
            </form>
          </div>
        </div>
      }

      @if (removeTarget(); as target) {
        <div class="ui-overlay" role="presentation">
          <div class="ui-dialog" role="dialog" aria-modal="true" aria-labelledby="remove-person">
            <h2 id="remove-person">{{ 'users.deleteTitle' | transloco }}</h2>
            <p class="ui-note">
              {{ 'users.deleteBody' | transloco: { email: target.email } }}
            </p>
            <ng-container *ngTemplateOutlet="problem" />
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
                      <app-select
                        [options]="roleOptions"
                        [value]="row.role"
                        [busy]="busy() || pendingId() === row.userId"
                        [ariaLabel]="('users.role' | transloco) + ' — ' + row.email"
                        (valueChange)="changeRole(row, $event)"
                      />
                    } @else {
                      <span class="ui-badge">{{ roleKey(row.role) | transloco }}</span>
                    }
                  </td>
                  <td>
                    <app-select
                      [options]="localeOptions"
                      [value]="row.locale"
                      [disabled]="!canManage() && row.userId !== me()"
                      [busy]="busy() || pendingId() === row.userId"
                      [ariaLabel]="('users.locale' | transloco) + ' — ' + row.email"
                      (valueChange)="changeLocale(row, $event)"
                    />
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
  private readonly notice = inject(NoticeService);
  private readonly fb = inject(FormBuilder);
  private readonly emailInput = viewChild<ElementRef<HTMLInputElement>>('createEmail');

  readonly roleOptions: readonly SelectOption[] = ROLES.map((role) => ({
    value: role.code,
    labelKey: role.key,
  }));
  readonly localeOptions: readonly SelectOption[] = [
    { value: 'ar', labelKey: 'locale.ar' },
    { value: 'en', labelKey: 'locale.en' },
  ];
  readonly rows = signal<UserRow[]>([]);
  readonly loading = signal(true);
  readonly error = signal<ApiProblem | null>(null);
  readonly creating = signal(false);
  readonly creatingBusy = signal(false);
  readonly busy = signal(false);
  /** The one row whose write is in flight (never the whole table). */
  readonly pendingId = signal<string | null>(null);
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

  /**
   * `quiet` refreshes in place: the table keeps its rows and never swaps for the
   * loading line, so a save does not blink the screen it just confirmed.
   */
  load(quiet = false): void {
    if (!quiet) this.loading.set(true);
    this.api.users(this.tenantId()).subscribe({
      next: (rows) => {
        this.rows.set(rows);
        this.loading.set(false);
      },
      error: (error: unknown) => {
        this.error.set(describeApiError(error));
        if (!quiet) this.rows.set([]);
        this.loading.set(false);
      },
    });
  }

  openCreate(): void {
    this.error.set(null);
    this.form.reset({ role: 'viewer', locale: 'en' });
    this.creating.set(true);
    // the dialog is a task, so the cursor starts on the first field
    setTimeout(() => this.emailInput()?.nativeElement.focus());
  }

  closeCreate(): void {
    if (this.creatingBusy()) return;
    this.creating.set(false);
    this.error.set(null);
    this.form.reset({ role: 'viewer', locale: 'en' });
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
          this.notice.show('toast.created', 'success');
          this.load();
        },
        error: (error: unknown) => {
          this.creatingBusy.set(false);
          this.error.set(describeApiError(error));
          this.notice.show(describeApiError(error).key, 'error');
        },
      });
  }

  changeRole(row: UserRow, role: string): void {
    if (role === row.role || this.pendingId()) return;
    const previous = row.role;
    this.pendingId.set(row.userId);
    this.error.set(null);
    this.patchRow(row.userId, { role });
    this.api.setRole(this.tenantId(), row.userId, role).subscribe({
      next: (updated) => {
        // the PATCH answer IS the row: paint it in place, do not reload the list
        this.replaceRow(updated);
        this.pendingId.set(null);
        this.notice.show('toast.roleChanged', 'success');
      },
      error: (error: unknown) => {
        // a refusal must not leave the select showing a value the door rejected
        this.patchRow(row.userId, { role: previous });
        this.failedMutation(error);
      },
    });
  }

  changeLocale(row: UserRow, locale: string): void {
    if (locale === row.locale || this.pendingId()) return;
    const previous = row.locale;
    this.pendingId.set(row.userId);
    this.error.set(null);
    this.patchRow(row.userId, { locale });
    this.api.setLocale(this.tenantId(), row.userId, locale).subscribe({
      next: (updated) => {
        this.replaceRow(updated);
        this.pendingId.set(null);
        this.notice.show('toast.localeChanged', 'success');
      },
      error: (error: unknown) => {
        this.patchRow(row.userId, { locale: previous });
        this.failedMutation(error);
      },
    });
  }

  remove(row: UserRow): void {
    this.busy.set(true);
    this.error.set(null);
    this.api.deleteUser(this.tenantId(), row.userId).subscribe({
      next: () => {
        this.removeTarget.set(null);
        this.notice.show('toast.removed', 'success');
        this.afterMutation();
      },
      error: (error: unknown) => this.failedMutation(error),
    });
  }

  roleKey(role: string): string {
    return ROLES.find((entry) => entry.code === role)?.key ?? 'roles.member';
  }

  /** The server's answer is the truth: refetch quietly instead of blinking. */
  private afterMutation(): void {
    this.busy.set(false);
    this.load(true);
  }

  private replaceRow(updated: UserRow): void {
    this.rows.update((rows) =>
      rows.map((row) => (row.userId === updated.userId ? updated : row)),
    );
  }

  private patchRow(userId: string, patch: Partial<UserRow>): void {
    this.rows.update((rows) =>
      rows.map((row) => (row.userId === userId ? { ...row, ...patch } : row)),
    );
  }

  private failedMutation(error: unknown): void {
    const problem = describeApiError(error);
    this.busy.set(false);
    this.pendingId.set(null);
    this.error.set(problem);
    this.notice.show(problem.key, 'error');
    this.load(true);
  }
}
