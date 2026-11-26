import { Routes } from '@angular/router';
import { authGuard } from './core/auth/auth.guard';
import { platformGuard } from './core/auth/platform.guard';
import { Shell } from './layout/shell/shell';
import { LoginPage } from './pages/login/login';
import { ForgotPasswordPage } from './pages/forgot-password/forgot-password';
import { ResetPasswordPage } from './pages/reset-password/reset-password';
import { AccountPage } from './pages/settings/account';
import { StubPage } from './pages/stub-page/stub-page';
import { UsersPage } from './pages/users/users';
import { AssetsPage } from './pages/assets/assets';
import { DevicesPage } from './pages/devices/devices';
import { RulesPage } from './pages/rules/rules';
import { DashboardPage } from './pages/dashboard/dashboard';
import { AlertsPage } from './pages/alerts/alerts';
import { AlertDetailPage } from './pages/alerts/alert-detail';

export const routes: Routes = [
  { path: 'login', component: LoginPage },
  // P5 spec 07: anonymous, like the sign-in door it hangs off.
  { path: 'forgot-password', component: ForgotPasswordPage },
  // The mailed link's landing page; the token is a path segment (A1).
  { path: 'reset-password/:token', component: ResetPasswordPage },
  {
    path: '',
    component: Shell,
    canActivate: [authGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'overview' },
      // P5 spec 03: the overview IS the live dashboard (FR-40).
      { path: 'overview', component: DashboardPage },
      { path: 'users', component: UsersPage },
      { path: 'assets', component: AssetsPage },
      { path: 'assets/:assetId/devices', component: DevicesPage },
      // P5 spec 04: the chart library must not enter the initial bundle (C9).
      {
        path: 'assets/:assetId/history',
        loadComponent: () =>
          import('./pages/asset-history/asset-history').then((m) => m.AssetHistoryPage),
      },
      { path: 'devices/:deviceId/rules', component: RulesPage },
      // P5 spec 05: the alert feed (FR-42) and one alert's evidence.
      { path: 'alerts', component: AlertsPage },
      { path: 'alerts/:alertId', component: AlertDetailPage },
      // P5 spec 07: settings exists now, starting with the account page (C5).
      { path: 'settings', pathMatch: 'full', redirectTo: 'settings/account' },
      { path: 'settings/account', component: AccountPage },
      {
        path: 'platform',
        component: StubPage,
        data: { page: 'platform' },
        canActivate: [platformGuard],
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
