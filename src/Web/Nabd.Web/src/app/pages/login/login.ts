import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { ActivatedRoute, Router } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { AuthService } from '../../core/auth/auth.service';
import { LocaleService } from '../../core/i18n/locale.service';

function errorCode(error: HttpErrorResponse): string | null {
  const body: unknown = error.error;
  if (body && typeof body === 'object' && 'code' in body) {
    const code = (body as { code: unknown }).code;
    return typeof code === 'string' ? code : null;
  }
  return null;
}

@Component({
  selector: 'app-login',
  imports: [ReactiveFormsModule, TranslocoPipe],
  templateUrl: './login.html',
  styleUrl: './login.scss',
})
export class LoginPage {
  private readonly auth = inject(AuthService);
  private readonly locale = inject(LocaleService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  readonly form = inject(FormBuilder).nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required]],
  });

  readonly submitting = signal(false);
  readonly errorKey = signal<string | null>(null);

  submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.submitting.set(true);
    this.errorKey.set(null);

    const { email, password } = this.form.getRawValue();
    this.auth.login(email, password).subscribe({
      next: (response) => {
        // Boot in the user's stored language before the shell renders (test 5).
        this.locale.init(response.user.locale);
        void this.router.navigateByUrl(this.returnUrl());
      },
      error: (error: unknown) => {
        this.submitting.set(false);
        this.errorKey.set(this.keyFor(error));
      },
    });
  }

  private keyFor(error: unknown): string {
    if (!(error instanceof HttpErrorResponse)) return 'errors.unexpected';
    if (error.status === 0) return 'errors.network';
    if (error.status === 429) return 'auth.backoff';

    const code = errorCode(error);
    if (code === 'identity:tenant_disabled') return 'auth.tenantDisabled';
    if (error.status === 401 || code === 'identity:invalid_credentials') return 'auth.invalid';

    return 'errors.unexpected';
  }

  /** Only same-app absolute paths are honoured — never `//host` or a full URL. */
  private returnUrl(): string {
    const value = this.route.snapshot.queryParamMap.get('returnUrl');
    if (value && value.startsWith('/') && !value.startsWith('//')) return value;
    return '/overview';
  }
}
