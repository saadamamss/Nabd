import { NgTemplateOutlet } from '@angular/common';
import { Component, computed, ElementRef, inject, OnInit, signal, viewChild } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiProblem, describeApiError } from '../../core/api/api-error';
import { DeviceMint, DeviceRow, TenantAdminApi } from '../../core/api/tenant-admin.service';
import { AuthService } from '../../core/auth/auth.service';
import { NoticeService } from '../../core/ui/notice.service';

/**
 * Devices (spec 02, A1): the token is shown once, in memory, and never
 * refetched — the row and every later GET carry no token at all.
 *
 * A revoked device is terminal in this version: the server sets
 * `is_active = false` and nothing sets it back, so reissue/revoke are not
 * offered on a revoked row (the reissued token would never authenticate).
 */
@Component({
  selector: 'app-devices-page',
  imports: [NgTemplateOutlet, ReactiveFormsModule, RouterLink, TranslocoPipe],
  template: `
    <section class="ui-page">
      <header class="ui-head">
        <div>
          <span class="ui-kicker">
            <a routerLink="/assets">{{ 'devices.backToAsset' | transloco }}</a>
          </span>
          <h1 class="ui-title">{{ 'devices.title' | transloco }}</h1>
          <p class="ui-sub">{{ 'devices.body' | transloco }}</p>
        </div>
        @if (canManage()) {
          <button class="ui-btn is-primary" type="button" (click)="openCreate()">
            {{ 'devices.add' | transloco }}
          </button>
        }
      </header>

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

      @if (!creating() && !minted() && !confirm()) {
        <ng-container *ngTemplateOutlet="problem" />
      }

      @if (creating()) {
        <div class="ui-overlay" role="presentation" (keydown.escape)="closeCreate()">
          <div class="ui-dialog" role="dialog" aria-modal="true" aria-labelledby="create-device">
            <h2 id="create-device">{{ 'devices.createTitle' | transloco }}</h2>
            <ng-container *ngTemplateOutlet="problem" />
            <form [formGroup]="form" (ngSubmit)="mint()">
              <div class="ui-grid">
                <label class="ui-field">
                  <span>{{ 'devices.name' | transloco }}</span>
                  <input #createName type="text" formControlName="name" required />
                </label>
              </div>
              <div class="ui-actions">
                <button class="ui-btn" type="button" (click)="closeCreate()" [disabled]="busy()">
                  {{ 'common.cancel' | transloco }}
                </button>
                <button class="ui-btn is-primary" type="submit" [disabled]="form.invalid || busy()">
                  {{ (busy() ? 'devices.creating' : 'devices.create') | transloco }}
                </button>
              </div>
            </form>
          </div>
        </div>
      }

      @if (minted(); as reveal) {
        <div class="ui-overlay" role="presentation">
          <div class="ui-dialog" role="dialog" aria-modal="true" aria-labelledby="token-title">
            <h2 id="token-title">{{ 'devices.tokenTitle' | transloco }}</h2>
            <ng-container *ngTemplateOutlet="problem" />
            <p class="ui-hint">{{ reveal.device.name }}</p>
            <p class="ui-note">{{ 'devices.tokenBody' | transloco }}</p>
            <code class="ui-token">{{ reveal.token }}</code>
            <p class="ui-note">{{ 'devices.tokenWarning' | transloco }}</p>
            <div class="ui-actions">
              <button class="ui-btn" type="button" (click)="copy(reveal.token)">
                {{ (copied() ? 'common.copied' : 'devices.copy') | transloco }}
              </button>
              <button class="ui-btn is-primary" type="button" (click)="minted.set(null)">
                {{ 'devices.done' | transloco }}
              </button>
            </div>
          </div>
        </div>
      }

      @if (confirm(); as target) {
        <div class="ui-overlay" role="presentation">
          <div class="ui-dialog" role="dialog" aria-modal="true" aria-labelledby="device-confirm">
            <h2 id="device-confirm">
              {{ (target.kind === 'reissue' ? 'devices.reissueTitle' : 'devices.revokeTitle') | transloco }}
            </h2>
            <p class="ui-note">
              {{
                (target.kind === 'reissue' ? 'devices.reissueBody' : 'devices.revokeBody')
                  | transloco: { name: target.device.name }
              }}
            </p>
            <ng-container *ngTemplateOutlet="problem" />
            <div class="ui-actions">
              <button class="ui-btn" type="button" (click)="confirm.set(null)">
                {{ 'common.cancel' | transloco }}
              </button>
              <button
                class="ui-btn"
                [class.is-danger]="target.kind === 'revoke'"
                [class.is-primary]="target.kind === 'reissue'"
                type="button"
                [disabled]="busy()"
                (click)="run(target)"
              >
                {{ (target.kind === 'reissue' ? 'devices.reissue' : 'devices.revoke') | transloco }}
              </button>
            </div>
          </div>
        </div>
      }

      @if (loading()) {
        <p class="ui-note">{{ 'common.loading' | transloco }}</p>
      } @else if (!rows().length) {
        <p class="ui-panel ui-empty">{{ 'devices.empty' | transloco }}</p>
      } @else {
        <div class="ui-table-wrap">
          <table class="ui-table">
            <thead>
              <tr>
                <th scope="col">{{ 'devices.name' | transloco }}</th>
                <th scope="col">{{ 'common.active' | transloco }}</th>
                @if (canManage()) {
                  <th scope="col">{{ 'common.actions' | transloco }}</th>
                }
              </tr>
            </thead>
            <tbody>
              @for (row of rows(); track row.deviceId) {
                <tr>
                  <td>
                    <a
                      [routerLink]="['/devices', row.deviceId, 'rules']"
                      [queryParams]="{ assetId: assetId, templateId: row.templateId }"
                    >
                      {{ row.name }}
                    </a>
                  </td>
                  <td>
                    @if (row.isActive && !row.tokenRevokedAtUtc) {
                      <span class="ui-badge">{{ 'devices.active' | transloco }}</span>
                    } @else {
                      <span class="ui-badge is-muted">{{ 'common.revoked' | transloco }}</span>
                    }
                  </td>
                  @if (canManage()) {
                    <td>
                      <div class="ui-actions">
                        @if (row.isActive && !row.tokenRevokedAtUtc) {
                          <button class="ui-btn is-small" type="button" (click)="ask('reissue', row)">
                            {{ 'devices.reissue' | transloco }}
                          </button>
                          <button class="ui-btn is-small is-danger" type="button" (click)="ask('revoke', row)">
                            {{ 'devices.revoke' | transloco }}
                          </button>
                        }
                      </div>
                    </td>
                  }
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </section>
  `,
})
export class DevicesPage implements OnInit {
  private readonly api = inject(TenantAdminApi);
  private readonly auth = inject(AuthService);
  private readonly notice = inject(NoticeService);
  private readonly route = inject(ActivatedRoute);
  private readonly fb = inject(FormBuilder);
  private readonly nameInput = viewChild<ElementRef<HTMLInputElement>>('createName');

  readonly assetId = this.route.snapshot.paramMap.get('assetId') ?? '';
  readonly rows = signal<DeviceRow[]>([]);
  readonly loading = signal(true);
  readonly error = signal<ApiProblem | null>(null);
  readonly creating = signal(false);
  readonly busy = signal(false);
  readonly minted = signal<DeviceMint | null>(null);
  readonly copied = signal(false);
  readonly confirm = signal<{ kind: 'reissue' | 'revoke'; device: DeviceRow } | null>(null);

  readonly canManage = computed(() => this.auth.user()?.role !== 'viewer');

  readonly form = this.fb.group({ name: ['', Validators.required] });

  ngOnInit(): void {
    this.load();
  }

  /** `quiet` refreshes in place — a save must not blink the table it confirms. */
  load(quiet = false): void {
    if (!quiet) this.loading.set(true);
    this.api.devices(this.assetId).subscribe({
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
    this.form.reset({ name: '' });
    this.creating.set(true);
    setTimeout(() => this.nameInput()?.nativeElement.focus());
  }

  closeCreate(): void {
    if (this.busy()) return;
    this.creating.set(false);
    this.error.set(null);
    this.form.reset({ name: '' });
  }

  mint(): void {
    if (this.form.invalid || this.busy()) return;
    this.busy.set(true);
    this.error.set(null);

    this.api.mintDevice(this.assetId, this.form.getRawValue().name ?? '').subscribe({
      next: (mint) => {
        this.busy.set(false);
        this.creating.set(false);
        this.form.reset({ name: '' });
        this.copied.set(false);
        this.minted.set(mint);
        this.notice.show('toast.deviceMinted', 'success');
        this.load(true);
      },
      error: (error: unknown) => {
        this.busy.set(false);
        this.error.set(describeApiError(error));
        this.notice.show(describeApiError(error).key, 'error');
      },
    });
  }

  ask(kind: 'reissue' | 'revoke', device: DeviceRow): void {
    this.error.set(null);
    this.confirm.set({ kind, device });
  }

  run(target: { kind: 'reissue' | 'revoke'; device: DeviceRow }): void {
    this.busy.set(true);
    this.error.set(null);

    if (target.kind === 'revoke') {
      this.api.revokeDevice(target.device.deviceId).subscribe({
        next: () => {
          this.busy.set(false);
          this.confirm.set(null);
          this.notice.show('toast.deviceRevoked', 'success');
          this.load(true);
        },
        error: (error: unknown) => {
          this.busy.set(false);
          this.error.set(describeApiError(error));
          this.notice.show(describeApiError(error).key, 'error');
          this.load(true);
        },
      });
      return;
    }

    this.api.reissueDevice(target.device.deviceId).subscribe({
      next: (mint) => {
        this.busy.set(false);
        this.confirm.set(null);
        this.copied.set(false);
        this.minted.set(mint);
        this.notice.show('toast.deviceReissued', 'success');
        this.load(true);
      },
      error: (error: unknown) => {
        this.busy.set(false);
        this.error.set(describeApiError(error));
        this.notice.show(describeApiError(error).key, 'error');
        this.load(true);
      },
    });
  }

  copy(token: string): void {
    void navigator.clipboard?.writeText(token).catch(() => undefined);
    this.copied.set(true);
  }
}
