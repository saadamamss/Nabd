import { NgTemplateOutlet } from '@angular/common';
import { Component, computed, ElementRef, inject, OnInit, signal, viewChild } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { ApiProblem, describeApiError } from '../../core/api/api-error';
import { AssetRow, TenantAdminApi, TemplateRow } from '../../core/api/tenant-admin.service';
import { AuthService } from '../../core/auth/auth.service';
import { NoticeService } from '../../core/ui/notice.service';
import { SelectComponent, type SelectOption } from '../../core/ui/select';

/**
 * Assets (spec 02): the template is picked once and then immutable — the
 * channels it fixes are the history's meaning. Archive, never hard delete.
 */
@Component({
  selector: 'app-assets-page',
  imports: [NgTemplateOutlet, ReactiveFormsModule, RouterLink, SelectComponent, TranslocoPipe],
  template: `
    <section class="ui-page">
      <header class="ui-head">
        <div>
          <span class="ui-kicker">{{ 'common.workspace' | transloco }}</span>
          <h1 class="ui-title">{{ 'assets.title' | transloco }}</h1>
          <p class="ui-sub">{{ 'assets.body' | transloco }}</p>
        </div>
        @if (canManage()) {
          <button class="ui-btn is-primary" type="button" (click)="openCreate()">
            {{ 'assets.add' | transloco }}
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

      @if (!creating() && !editing() && !archiveTarget()) {
        <ng-container *ngTemplateOutlet="problem" />
      }

      @if (creating()) {
        <div class="ui-overlay" role="presentation" (keydown.escape)="closeCreate()">
          <div class="ui-dialog" role="dialog" aria-modal="true" aria-labelledby="create-asset">
            <h2 id="create-asset">{{ 'assets.createTitle' | transloco }}</h2>
            <ng-container *ngTemplateOutlet="problem" />
            <form [formGroup]="form" (ngSubmit)="create()">
              <div class="ui-grid">
                <div class="ui-field">
                  <span>{{ 'assets.template' | transloco }}</span>
                  <app-select
                    #createTemplate
                    formControlName="templateId"
                    [options]="templateOptions()"
                    [required]="true"
                    [ariaLabel]="'assets.template' | transloco"
                    (valueChange)="templateChanged()"
                  />
                  <small>{{ 'assets.templateHint' | transloco }}</small>
                </div>
                <label class="ui-field">
                  <span>{{ 'assets.name' | transloco }}</span>
                  <input #createName type="text" formControlName="name" required />
                </label>
                <label class="ui-field">
                  <span>{{ 'assets.location' | transloco }}</span>
                  <input type="text" formControlName="location" />
                </label>
                <label class="ui-field">
                  <span>{{ 'assets.notes' | transloco }}</span>
                  <input type="text" formControlName="notes" />
                </label>
              </div>

              @if (selectedTemplate(); as template) {
                <p class="ui-hint">{{ 'assets.channels' | transloco }}:</p>
                <p class="ui-hint">
                  @for (channel of template.channels; track channel.key) {
                    <span class="ui-badge">
                      {{ channelName(channel) }} · {{ channel.unit }}
                    </span>
                  }
                </p>
              }

              <div class="ui-actions">
                <button class="ui-btn" type="button" (click)="closeCreate()" [disabled]="busy()">
                  {{ 'common.cancel' | transloco }}
                </button>
                <button class="ui-btn is-primary" type="submit" [disabled]="form.invalid || busy()">
                  {{ (busy() ? 'assets.creating' : 'assets.create') | transloco }}
                </button>
              </div>
            </form>
          </div>
        </div>
      }

      @if (editing(); as row) {
        <div class="ui-overlay" role="presentation" (keydown.escape)="editing.set(null)">
          <div class="ui-dialog" role="dialog" aria-modal="true" aria-labelledby="edit-asset">
            <h2 id="edit-asset">{{ 'assets.editTitle' | transloco }}</h2>
            <ng-container *ngTemplateOutlet="problem" />
            <form [formGroup]="editForm" (ngSubmit)="saveEdit(row)">
              <div class="ui-grid">
                <label class="ui-field">
                  <span>{{ 'assets.name' | transloco }}</span>
                  <input type="text" formControlName="name" required />
                </label>
                <label class="ui-field">
                  <span>{{ 'assets.location' | transloco }}</span>
                  <input type="text" formControlName="location" />
                </label>
                <label class="ui-field">
                  <span>{{ 'assets.notes' | transloco }}</span>
                  <input type="text" formControlName="notes" />
                </label>
                <label class="ui-field">
                  <span>{{ 'assets.templateLocked' | transloco }}</span>
                  <input type="text" [value]="templateNameFor(row.templateId)" disabled />
                </label>
              </div>
              <div class="ui-actions">
                <button class="ui-btn" type="button" (click)="editing.set(null)" [disabled]="busy()">
                  {{ 'common.cancel' | transloco }}
                </button>
                <button class="ui-btn is-primary" type="submit" [disabled]="editForm.invalid || busy()">
                  {{ (busy() ? 'assets.saving' : 'assets.save') | transloco }}
                </button>
              </div>
            </form>
          </div>
        </div>
      }

      @if (archiveTarget(); as target) {
        <div class="ui-overlay" role="presentation">
          <div class="ui-dialog" role="dialog" aria-modal="true" aria-labelledby="archive-asset">
            <h2 id="archive-asset">{{ 'assets.archiveTitle' | transloco }}</h2>
            <p class="ui-note">{{ 'assets.archiveBody' | transloco: { name: target.name } }}</p>
            <ng-container *ngTemplateOutlet="problem" />
            <div class="ui-actions">
              <button class="ui-btn" type="button" (click)="archiveTarget.set(null)">
                {{ 'common.cancel' | transloco }}
              </button>
              <button class="ui-btn is-danger" type="button" (click)="archive(target)" [disabled]="busy()">
                {{ 'assets.archive' | transloco }}
              </button>
            </div>
          </div>
        </div>
      }

      <div class="ui-toolbar">
        <label class="ui-check">
          <input type="checkbox" [checked]="includeArchived()" (change)="toggleArchived($event)" />
          <span class="ui-check-box" aria-hidden="true"></span>
          {{ 'assets.showArchived' | transloco }}
        </label>
      </div>

      @if (loading()) {
        <p class="ui-note">{{ 'common.loading' | transloco }}</p>
      } @else if (!rows().length) {
        <p class="ui-panel ui-empty">{{ 'assets.empty' | transloco }}</p>
      } @else {
        <div class="ui-table-wrap">
          <table class="ui-table">
            <thead>
              <tr>
                <th scope="col">{{ 'assets.name' | transloco }}</th>
                <th scope="col">{{ 'assets.template' | transloco }}</th>
                <th scope="col">{{ 'assets.location' | transloco }}</th>
                <th scope="col">{{ 'common.actions' | transloco }}</th>
              </tr>
            </thead>
            <tbody>
              @for (row of rows(); track row.assetId) {
                <tr>
                  <td>
                    <strong>{{ row.name }}</strong>
                    @if (!row.isActive) {
                      <span class="ui-badge is-muted">{{ 'assets.archivedTag' | transloco }}</span>
                    }
                  </td>
                  <td>{{ templateNameFor(row.templateId) }}</td>
                  <td>{{ row.location || ('assets.none' | transloco) }}</td>
                  <td>
                    <div class="ui-actions">
                      <a class="ui-btn is-small" [routerLink]="['/assets', row.assetId, 'devices']">
                        {{ 'assets.openDevices' | transloco }}
                      </a>
                      @if (canManage()) {
                        <button class="ui-btn is-small" type="button" (click)="edit(row)">
                          {{ 'common.edit' | transloco }}
                        </button>
                        @if (row.isActive) {
                          <button class="ui-btn is-small is-danger" type="button" (click)="archiveTarget.set(row)">
                            {{ 'assets.archive' | transloco }}
                          </button>
                        }
                      }
                    </div>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </section>
  `,
})
export class AssetsPage implements OnInit {
  private readonly api = inject(TenantAdminApi);
  private readonly auth = inject(AuthService);
  private readonly notice = inject(NoticeService);
  private readonly fb = inject(FormBuilder);
  private readonly nameInput = viewChild<ElementRef<HTMLInputElement>>('createName');
  private readonly templateSelect = viewChild<SelectComponent>('createTemplate');
  private readonly transloco = inject(TranslocoService);

  readonly rows = signal<AssetRow[]>([]);
  readonly templates = signal<TemplateRow[]>([]);
  readonly loading = signal(true);
  readonly error = signal<ApiProblem | null>(null);
  readonly creating = signal(false);
  readonly busy = signal(false);
  readonly includeArchived = signal(false);
  readonly editing = signal<AssetRow | null>(null);
  readonly archiveTarget = signal<AssetRow | null>(null);
  readonly selectedTemplateId = signal('');

  readonly canManage = computed(() => this.auth.user()?.role !== 'viewer');

  readonly form = this.fb.group({
    templateId: ['', Validators.required],
    name: ['', Validators.required],
    location: [''],
    notes: [''],
  });

  readonly editForm = this.fb.group({
    name: ['', Validators.required],
    location: [''],
    notes: [''],
  });

  readonly selectedTemplate = computed(() =>
    this.templates().find((template) => template.templateId === this.selectedTemplateId()),
  );

  readonly templateOptions = computed<readonly SelectOption[]>(() => {
    const arabic = this.transloco.activeLang() === 'ar';
    return this.templates().map((template) => ({
      value: template.templateId,
      label: arabic ? template.nameAr : template.nameEn,
    }));
  });

  ngOnInit(): void {
    this.load();
    this.api.templates().subscribe({
      next: (templates) => this.templates.set(templates),
      error: (error: unknown) => this.error.set(describeApiError(error)),
    });
  }

  /** `quiet` refreshes in place — a save must not blink the table it confirms. */
  load(quiet = false): void {
    if (!quiet) this.loading.set(true);
    this.api.assets(this.includeArchived()).subscribe({
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
    this.selectedTemplateId.set('');
    this.form.reset({ templateId: '', name: '', location: '', notes: '' });
    this.creating.set(true);
    // the template is the first field and the one the form cannot go without
    setTimeout(() => this.templateSelect()?.focus());
  }

  closeCreate(): void {
    if (this.busy()) return;
    this.creating.set(false);
    this.error.set(null);
    this.selectedTemplateId.set('');
    this.form.reset({ templateId: '', name: '', location: '', notes: '' });
  }

  templateChanged(): void {
    this.selectedTemplateId.set(this.form.getRawValue().templateId ?? '');
  }

  create(): void {
    if (this.form.invalid || this.busy()) return;
    this.busy.set(true);
    this.error.set(null);

    const value = this.form.getRawValue();
    this.api
      .createAsset({
        templateId: value.templateId ?? '',
        name: value.name ?? '',
        location: value.location || null,
        notes: value.notes || null,
      })
      .subscribe({
        next: () => {
          this.busy.set(false);
          this.creating.set(false);
          this.selectedTemplateId.set('');
          this.notice.show('toast.assetCreated', 'success');
          this.load(true);
        },
        error: (error: unknown) => {
          this.busy.set(false);
          this.error.set(describeApiError(error));
          this.notice.show(describeApiError(error).key, 'error');
        },
      });
  }

  edit(row: AssetRow): void {
    this.editing.set(row);
    this.editForm.reset({ name: row.name, location: row.location ?? '', notes: row.notes ?? '' });
    this.error.set(null);
  }

  saveEdit(row: AssetRow): void {
    if (this.editForm.invalid || this.busy()) return;
    this.busy.set(true);
    const value = this.editForm.getRawValue();

    this.api
      .updateAsset(row.assetId, {
        name: value.name ?? '',
        location: value.location || null,
        notes: value.notes || null,
      })
      .subscribe({
        next: () => {
          this.busy.set(false);
          this.editing.set(null);
          this.notice.show('toast.assetUpdated', 'success');
          this.load(true);
        },
        error: (error: unknown) => {
          this.busy.set(false);
          this.error.set(describeApiError(error));
          this.notice.show(describeApiError(error).key, 'error');
        },
      });
  }

  archive(row: AssetRow): void {
    this.busy.set(true);
    this.error.set(null);
    this.api.archiveAsset(row.assetId).subscribe({
      next: () => {
        this.busy.set(false);
        this.archiveTarget.set(null);
        this.notice.show('toast.assetArchived', 'success');
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

  toggleArchived(event: Event): void {
    this.includeArchived.set((event.target as HTMLInputElement).checked);
    this.load(true);
  }

  templateName(template: TemplateRow): string {
    return this.isArabic() ? template.nameAr : template.nameEn;
  }

  channelName(channel: { nameAr: string; nameEn: string }): string {
    return this.isArabic() ? channel.nameAr : channel.nameEn;
  }

  templateNameFor(templateId: string): string {
    const template = this.templates().find((item) => item.templateId === templateId);
    return template ? this.templateName(template) : templateId;
  }

  private isArabic(): boolean {
    return this.transloco.activeLang() === 'ar';
  }
}
