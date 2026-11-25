import { Routes } from '@angular/router';
import { authGuard } from './core/auth/auth.guard';
import { platformGuard } from './core/auth/platform.guard';
import { Shell } from './layout/shell/shell';
import { LoginPage } from './pages/login/login';
import { StubPage } from './pages/stub-page/stub-page';
import { UsersPage } from './pages/users/users';
import { AssetsPage } from './pages/assets/assets';
import { DevicesPage } from './pages/devices/devices';
import { RulesPage } from './pages/rules/rules';
import { DashboardPage } from './pages/dashboard/dashboard';

export const routes: Routes = [
  { path: 'login', component: LoginPage },
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
      { path: 'alerts', component: StubPage, data: { page: 'alerts' } },
      { path: 'settings', component: StubPage, data: { page: 'settings' } },
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
