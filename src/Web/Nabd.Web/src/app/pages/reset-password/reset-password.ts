import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { AuthService } from '../../core/auth/auth.service';
import { describeApiError } from '../../core/api/api-error';
import { AuthCard } from '../auth/auth-card';

/** The same throttle fact as the request page — one sentence, one key. */
const RETRY_KEY = 'auth.forgotRetry';

const DEAD_STATUS = 410;
const WEAK_STATUS = 400;
const DEAD_CODE = 'identity:reset_token_dead';
const WEAK_CODE = 'identity:weak_password';

function codeOf(error: HttpErrorResponse): string | null {
  const body: unknown = error.error;
  if (body && typeof body === 'object' && 'code' in body) {
    const code = (body as { code: unknown }).code;
    if (typeof code === 'string') return code;
  }
  return null;
}

function delaySeconds(error: HttpErrorResponse): number | null {
  const body: unknown = error.error;
  if (body && typeof body === 'object' && 'delaySeconds' in body) {
    const value = (body as { delaySeconds: unknown }).delaySeconds;
    if (typeof value === 'number' && Number.isFinite(value)) return value;
  }
  return null;
}

/**
 * `/reset-password/:token` (spec 07, A5): the token is read once from the path
 * into memory, never rendered, never logged, and scrubbed from the address bar
 * after a successful submit. The door owns every rule — the form adds only the
 * confirm-match check (A4).
 */
@Component({
  selector: 'app-reset-password',
  imports: [ReactiveFormsModule, RouterLink, TranslocoPipe, AuthCard],
  templateUrl: './reset-password.html',
  styleUrl: './reset-password.scss',
})
export class ResetPasswordPage {
  private readonly auth = inject(AuthService);
  private readonly route = inject(ActivatedRoute);

  /** Read once from the path; it stays in this field and nowhere else (A5). */
  private readonly token = this.route.snapshot.paramMap.get('token') ?? '';

  readonly form = inject(FormBuilder).nonNullable.group({
    password: ['', [Validators.required]],
    confirm: ['', [Validators.required]],
  });

  readonly sending = signal(false);
  readonly done = signal(false);
  readonly dead = signal(false);
  readonly mismatch = signal(false);
  readonly passwordProblem = signal<string | null>(null);
  readonly problemKey = signal<string | null>(null);
  readonly retrySeconds = signal<number | null>(null);

  submit(): void {
    if (this.sending() || this.done() || this.dead()) return;

    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const { password, confirm } = this.form.getRawValue();

    // a typo never reaches a single-use door: the token would burn on a password
    // the visitor did not mean to set
    this.mismatch.set(password !== confirm);
    if (this.mismatch()) return;

    this.sending.set(true);
    this.passwordProblem.set(null);
    this.problemKey.set(null);
    this.retrySeconds.set(null);

    // sent exactly as typed; the door trims on this path (C4 — recorded, not patched)
    this.auth.confirmReset(this.token, password).subscribe({
      next: () => this.succeed(),
      error: (error: unknown) => {
        this.sending.set(false);
        this.report(error);
      },
    });
  }

  /** Typing clears the mismatch line instead of leaving it stale under the field. */
  clearMismatch(): void {
    if (this.mismatch()) this.mismatch.set(false);
  }

  private succeed(): void {
    this.sending.set(false);
    this.done.set(true);
    this.form.reset({ password: '', confirm: '' }); // a password does not linger in a form

    // every refresh token is revoked server-side; a signed-in tab must stop
    // claiming a session instead of lying for up to 15 minutes (C8). No /logout:
    // the cookie is already dead, so that call would be theatre.
    if (this.auth.user()) this.auth.clearLocal();

    // The token must leave the address bar (A5). `/reset-password` matches no
    // route, so a reload lands at the sign-in door — never on a false panel (C9).
    try {
      history.replaceState(null, '', '/reset-password');
    } catch {
      // a locked-down history still leaves the success panel standing
    }
  }

  private report(error: unknown): void {
    if (!(error instanceof HttpErrorResponse)) {
      this.problemKey.set(describeApiError(error).key);
      return;
    }

    const status = error.status;
    const code = codeOf(error);

    // the door's own language first, one branch per fact (C2): a used/expired
    // link is dead, a short password is a rule violation, and neither is a
    // generic "something went wrong"
    if (status === DEAD_STATUS || code === DEAD_CODE) {
      this.dead.set(true);
      this.form.reset({ password: '', confirm: '' });
      return;
    }

    if (status === WEAK_STATUS || code === WEAK_CODE) {
      this.passwordProblem.set('errors.weakPassword');
      return;
    }

    if (status === 429) {
      this.retrySeconds.set(delaySeconds(error));
      this.problemKey.set(this.retrySeconds() === null ? 'auth.backoff' : RETRY_KEY);
      return;
    }

    this.problemKey.set(describeApiError(error).key);
  }
}
