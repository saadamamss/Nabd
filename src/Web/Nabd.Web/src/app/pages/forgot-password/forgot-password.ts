import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { AuthService } from '../../core/auth/auth.service';
import { describeApiError } from '../../core/api/api-error';
import { AuthCard } from '../auth/auth-card';

/** The 429 sentence takes the server's own seconds; with none, the plain line. */
const RETRY_KEY = 'auth.forgotRetry';
const BACKOFF_KEY = 'auth.backoff';

function delaySeconds(error: HttpErrorResponse): number | null {
  const body: unknown = error.error;
  if (body && typeof body === 'object' && 'delaySeconds' in body) {
    const value = (body as { delaySeconds: unknown }).delaySeconds;
    if (typeof value === 'number' && Number.isFinite(value)) return value;
  }
  return null;
}

/**
 * `/forgot-password` (spec 07, A3/A6): one email field, one neutral answer for
 * every address. The door answers 204 whether or not the account exists, so
 * this screen has nothing to distinguish — and must not invent a distinction.
 */
@Component({
  selector: 'app-forgot-password',
  imports: [ReactiveFormsModule, RouterLink, TranslocoPipe, AuthCard],
  templateUrl: './forgot-password.html',
  styleUrl: './forgot-password.scss',
})
export class ForgotPasswordPage {
  private readonly auth = inject(AuthService);

  readonly form = inject(FormBuilder).nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
  });

  readonly sending = signal(false);
  readonly sent = signal(false);
  readonly problemKey = signal<string | null>(null);
  readonly retrySeconds = signal<number | null>(null);

  submit(): void {
    if (this.sending()) return;

    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.sending.set(true);
    this.problemKey.set(null);
    this.retrySeconds.set(null);

    // the address travels exactly as typed — the server is the only judge
    this.auth.requestReset(this.form.getRawValue().email).subscribe({
      next: () => {
        this.sending.set(false);
        this.sent.set(true);
      },
      error: (error: unknown) => {
        this.sending.set(false);
        this.report(error);
      },
    });
  }

  private report(error: unknown): void {
    // A 429 is this page's own normal outcome (C6): show the server's seconds
    // verbatim, never a guessed wait, and keep the typed address for a retry.
    if (error instanceof HttpErrorResponse && error.status === 429) {
      this.retrySeconds.set(delaySeconds(error));
      this.problemKey.set(this.retrySeconds() === null ? BACKOFF_KEY : RETRY_KEY);
      return;
    }

    this.problemKey.set(describeApiError(error).key);
  }
}
