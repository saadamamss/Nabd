import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

/**
 * P5 spec 08 (C1): there is no user menu, so the settings surface is entered
 * through the shell's Settings item and moves between its pages here.
 *
 * Two real routes, never a tab strip that fakes state: the active link says
 * `aria-current="page"`, and every property is logical so RTL mirrors.
 */
@Component({
  selector: 'app-settings-tabs',
  imports: [RouterLink, RouterLinkActive, TranslocoPipe],
  template: `
    <nav class="settings-tabs" [attr.aria-label]="'settings.tabsLabel' | transloco">
      <a
        class="settings-tab"
        routerLink="/settings/account"
        routerLinkActive="is-active"
        ariaCurrentWhenActive="page"
        data-tab="account"
      >
        {{ 'settings.accountTab' | transloco }}
      </a>
      <a
        class="settings-tab"
        routerLink="/settings/notifications"
        routerLinkActive="is-active"
        ariaCurrentWhenActive="page"
        data-tab="notifications"
      >
        {{ 'settings.notificationsTab' | transloco }}
      </a>
    </nav>
  `,
  styles: `
    .settings-tabs {
      display: flex;
      flex-wrap: wrap;
      gap: 0.25rem;
      margin-block-end: 1rem;
      border-block-end: 1px solid var(--app-border);
    }

    .settings-tab {
      padding-block: 0.5rem;
      padding-inline: 0.9rem;
      border-block-end: 2px solid transparent;
      color: var(--app-text-muted);
      font-size: 0.875rem;
      font-weight: 500;
      text-decoration: none;
    }

    .settings-tab:hover {
      color: var(--app-text);
    }

    .settings-tab.is-active {
      border-block-end-color: var(--app-brand);
      color: var(--app-text);
      font-weight: 600;
    }
  `,
})
export class SettingsTabs {}
