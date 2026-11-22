import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, switchMap, throwError } from 'rxjs';
import { NoticeService } from '../ui/notice.service';
import { AuthService } from './auth.service';

const AUTH_DOORS = ['/api/v1/auth/login', '/api/v1/auth/refresh', '/api/v1/auth/logout'];

function is401(error: unknown): error is HttpErrorResponse {
  return error instanceof HttpErrorResponse && error.status === 401;
}

function errorCode(error: HttpErrorResponse): string | null {
  const body: unknown = error.error;
  if (body && typeof body === 'object' && 'code' in body) {
    const code = (body as { code: unknown }).code;
    return typeof code === 'string' ? code : null;
  }
  return null;
}

/**
 * Bearer + refresh-once + rotation (spec 01, "Auth mechanics").
 * The retried request never re-enters this interceptor, so one request buys at
 * most one refresh; a second 401 is terminal.
 */
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  const notice = inject(NoticeService);

  if (AUTH_DOORS.some((door) => request.url.includes(door))) {
    return next(request);
  }

  const withToken = (token: string | null) =>
    token ? request.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : request;

  const endSession = (error: HttpErrorResponse) => {
    auth.clearLocal();
    const disabled = errorCode(error) === 'identity:tenant_disabled';
    notice.show(disabled ? 'auth.tenantDisabled' : 'auth.sessionExpired');
    void router.navigate(['/login'], {
      queryParams: disabled ? {} : { returnUrl: router.url },
    });
    return throwError(() => error);
  };

  return next(withToken(auth.accessToken())).pipe(
    catchError((error: unknown) => {
      if (!is401(error)) return throwError(() => error);

      return auth.refresh().pipe(
        switchMap(() => next(withToken(auth.accessToken()))),
        catchError((second: unknown) =>
          endSession(second instanceof HttpErrorResponse ? second : error),
        ),
      );
    }),
  );
};
