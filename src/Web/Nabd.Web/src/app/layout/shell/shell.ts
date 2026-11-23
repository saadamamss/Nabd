import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { PLATFORM_ADMIN } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { LocaleService } from '../../core/i18n/locale.service';
import { TenantService } from '../../core/tenant/tenant.service';

interface NavItem {
  path: string;
  key: string;
  /** Stroke-only SVG path data, rendered as one icon. */
  icons: readonly string[];
  platformOnly?: boolean;
}

const ROLE_KEYS: Readonly<Record<string, string>> = {
  viewer: 'roles.viewer',
  tenant_admin: 'roles.tenantAdmin',
  platform_admin: 'roles.platformAdmin',
};

/** The rail is a workspace preference, not session state: it survives reloads. */
const RAIL_KEY = 'nabd.sidebar.collapsed';

function readRail(): boolean {
  try {
    return localStorage.getItem(RAIL_KEY) === '1';
  } catch {
    return false; // storage can be blocked; the rail simply starts expanded
  }
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

  readonly menuOpen = signal(false);
  readonly collapsed = signal(readRail());

  private readonly navItems: readonly NavItem[] = [
    {
      path: '/overview',
      key: 'nav.overview',
      icons: ['M4 4h6.5v6.5H4Z', 'M13.5 4H20v6.5h-6.5Z', 'M4 13.5h6.5V20H4Z', 'M13.5 13.5H20V20h-6.5Z'],
    },
    {
      path: '/assets',
      key: 'nav.assets',
      icons: ['M12 3 3 8l9 5 9-5-9-5Z', 'M3 12.5 12 17.5l9-5', 'M3 16.5 12 21.5l9-5'],
    },
    {
      path: '/alerts',
      key: 'nav.alerts',
      icons: ['M18 8.5a6 6 0 1 0-12 0c0 6.5-2.5 7.5-2.5 7.5h17S18 15 18 8.5Z', 'M13.7 19.5a2 2 0 0 1-3.4 0'],
    },
    {
      path: '/settings',
      key: 'nav.settings',
      icons: ['M4 7h8', 'M16 7h4', 'M4 17h4', 'M12 17h8', 'M14 5a2 2 0 1 0 0 4 2 2 0 0 0 0-4Z', 'M8 15a2 2 0 1 0 0 4 2 2 0 0 0 0-4Z'],
    },
    {
      path: '/platform',
      key: 'nav.platform',
      platformOnly: true,
      icons: ['M4 4.5h16v6H4Z', 'M4 13.5h16v6H4Z', 'M7.5 7.5h.01', 'M7.5 16.5h.01'],
    },
  ];

  /** Role-aware nav: dead ends are avoided, every grant still stays server-side. */
  readonly visibleNav = computed(() => {
    const isPlatformAdmin = this.auth.user()?.role === PLATFORM_ADMIN;
    return this.navItems.filter((item) => !item.platformOnly || isPlatformAdmin);
  });

  readonly roleKey = computed(() => ROLE_KEYS[this.auth.user()?.role ?? ''] ?? 'roles.member');

  /** Two-letter avatar from the local part of the email; never a blank circle. */
  readonly initials = computed(() => {
    const email = this.auth.user()?.email ?? '';
    const local = (email.split('@')[0] ?? '').replace(/[^\p{L}\p{N}]+/gu, ' ').trim();
    const parts = local.split(/\s+/).filter(Boolean);
    const text = parts.length > 1 ? `${parts[0][0]}${parts[1][0]}` : local.slice(0, 2);
    return text.toUpperCase() || 'N';
  });

  ngOnInit(): void {
    const user = this.auth.user();
    if (user) this.tenants.load(user.tenantId).subscribe();
  }

  toggleMenu(): void {
    this.menuOpen.update((open) => !open);
  }

  toggleSidebar(): void {
    this.collapsed.update((value) => {
      const next = !value;
      try {
        localStorage.setItem(RAIL_KEY, next ? '1' : '0');
      } catch {
        // private mode: the rail still toggles for this view
      }
      return next;
    });
  }

  closeMenu(): void {
    this.menuOpen.set(false);
  }

  signOut(): void {
    this.auth.logout().subscribe(() => void this.router.navigate(['/login']));
  }
}
