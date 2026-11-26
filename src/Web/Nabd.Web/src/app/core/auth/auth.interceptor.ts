import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, switchMap, throwError } from 'rxjs';
import { NoticeService } from '../ui/notice.service';
import { AuthService } from './auth.service';

const AUTH_DOORS = ['/api/v1/auth/login', '/api/v1/auth/refresh', '/api/v1/auth/logout'];

/**
 * The password-change door. Its 401 is a *credential* answer (wrong current
 * password), not an expired token: refreshing would rotate the cookie, retry
 * the PATCH, meet the same 401 and end the session with a false "session
 * expired" notice while the user is still perfectly signed in (spec 07, C1).
 */
const CHANGE_DOOR = /\/api\/v1\/auth\/[^/]+\/users\/[^/]+\/password$/;

function isChangeDoor(request: HttpRequest<unknown>): boolean {
  return request.method === 'PATCH' && CHANGE_DOOR.test(request.url);
}

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

      // the door's own denial: surface it, keep the session, no refresh
      if (isChangeDoor(request) && errorCode(error) === 'identity:invalid_credentials') {
        return throwError(() => error);
      }

      return auth.refresh().pipe(
        switchMap(() => next(withToken(auth.accessToken()))),
        catchError((second: unknown) =>
          endSession(second instanceof HttpErrorResponse ? second : error),
        ),
      );
    }),
  );
};
