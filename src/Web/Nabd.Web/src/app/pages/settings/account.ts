import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { Router } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { AuthService } from '../../core/auth/auth.service';
import { NoticeService } from '../../core/ui/notice.service';
import { describeApiError } from '../../core/api/api-error';
import { SettingsTabs } from './settings-tabs';

const WRONG_CURRENT = 'identity:invalid_credentials';
const WEAK = 'identity:weak_password';

function codeOf(error: HttpErrorResponse): string | null {
  const body: unknown = error.error;
  if (body && typeof body === 'object' && 'code' in body) {
    const code = (body as { code: unknown }).code;
    if (typeof code === 'string') return code;
  }
  return null;
}

/**
 * `/settings/account` (spec 07, A2/A7): self only, ids from the token snapshot
 * (the service takes them, never this form). A changed password signs out every
 * session server-side, so the screen signs out honestly too — no /logout call,
 * because the cookie is already dead.
 */
@Component({
  selector: 'app-account',
  imports: [ReactiveFormsModule, TranslocoPipe, SettingsTabs],
  templateUrl: './account.html',
  styleUrl: './account.scss',
})
export class AccountPage {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly notice = inject(NoticeService);

  readonly form = inject(FormBuilder).nonNullable.group({
    current: ['', [Validators.required]],
    password: ['', [Validators.required]],
    confirm: ['', [Validators.required]],
  });

  readonly saving = signal(false);
  readonly mismatch = signal(false);
  readonly currentProblem = signal<string | null>(null);
  readonly passwordProblem = signal<string | null>(null);
  readonly problemKey = signal<string | null>(null);

  submit(): void {
    if (this.saving()) return;

    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const { current, password, confirm } = this.form.getRawValue();

    // client-side only (A4): a typo must not spend a rotation on the server
    this.mismatch.set(password !== confirm);
    if (this.mismatch()) return;

    this.saving.set(true);
    this.currentProblem.set(null);
    this.passwordProblem.set(null);
    this.problemKey.set(null);

    this.auth.changePassword(current, password).subscribe({
      next: () => this.succeed(),
      error: (error: unknown) => {
        this.saving.set(false);
        this.report(error);
      },
    });
  }

  clearProblems(): void {
    if (this.mismatch()) this.mismatch.set(false);
    if (this.currentProblem()) this.currentProblem.set(null);
    if (this.passwordProblem()) this.passwordProblem.set(null);
  }

  private succeed(): void {
    this.saving.set(false);
    this.form.reset({ current: '', password: '', confirm: '' });

    // every refresh token was revoked by the door; keeping the user "signed in"
    // would lie for up to 15 minutes (A2). No /logout: the cookie is already dead.
    this.auth.clearLocal();
    this.notice.show('auth.passwordChanged');
    void this.router.navigate(['/login']);
  }

  private report(error: unknown): void {
    if (!(error instanceof HttpErrorResponse)) {
      this.problemKey.set(describeApiError(error).key);
      return;
    }

    const code = codeOf(error);

    // the door's own codes first (C2): "wrong current" and "too short" are two
    // different facts, each anchored at its own field
    if (code === WRONG_CURRENT) {
      this.currentProblem.set('settings.wrongCurrent');
      return;
    }

    if (code === WEAK) {
      this.passwordProblem.set('errors.weakPassword');
      return;
    }

    this.problemKey.set(describeApiError(error).key);
  }
}
