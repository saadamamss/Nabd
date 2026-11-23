import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { PLATFORM_ADMIN } from './auth.models';
import { AuthService } from './auth.service';

/** Hides a dead end: the Platform area is for `platform_admin` only (spec 09). */
export const platformGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  if (auth.user()?.role === PLATFORM_ADMIN) return true;

  return inject(Router).createUrlTree(['/overview']);
};
