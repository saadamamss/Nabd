import { Component, computed, inject, OnInit } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { PLATFORM_ADMIN } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { LocaleService } from '../../core/i18n/locale.service';
import { TenantService } from '../../core/tenant/tenant.service';

interface NavItem {
  path: string;
  key: string;
  platformOnly?: boolean;
}

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, TranslocoPipe],
  templateUrl: './shell.html',
  styleUrl: './shell.scss',
})
export class Shell implements OnInit {
  readonly auth = inject(AuthService);
  readonly locale = inject(LocaleService);
  readonly tenantName = inject(TenantService).name;

  private readonly tenants = inject(TenantService);
  private readonly router = inject(Router);

  private readonly navItems: readonly NavItem[] = [
    { path: '/overview', key: 'nav.overview' },
    { path: '/assets', key: 'nav.assets' },
    { path: '/alerts', key: 'nav.alerts' },
    { path: '/settings', key: 'nav.settings' },
    { path: '/platform', key: 'nav.platform', platformOnly: true },
  ];

  /** Role-aware nav: dead ends are avoided, every grant still stays server-side. */
  readonly visibleNav = computed(() => {
    const isPlatformAdmin = this.auth.user()?.role === PLATFORM_ADMIN;
    return this.navItems.filter((item) => !item.platformOnly || isPlatformAdmin);
  });

  ngOnInit(): void {
    const user = this.auth.user();
    if (user) this.tenants.load(user.tenantId).subscribe();
  }

  signOut(): void {
    this.auth.logout().subscribe(() => void this.router.navigate(['/login']));
  }
}
