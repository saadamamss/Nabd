import { Component } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

/**
 * P5 spec 05, E9 — "this episode opened muted, so nobody was notified".
 *
 * A server fact on the row (`QuietHoursSuppressed`), drawn in both the list and
 * the detail. It stands on its own: never part of the status pill, never cleared
 * by an ack, and never fed by the events door.
 */
@Component({
  selector: 'app-quiet-hours-badge',
  imports: [TranslocoPipe],
  template: `
    <span class="ui-badge is-warn" data-quiet-hours="true">
      <svg
        class="quiet-icon"
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        stroke-width="2"
        stroke-linecap="round"
        stroke-linejoin="round"
        aria-hidden="true"
      >
        <path d="M4 4 20 20" />
        <path d="M8.7 4.9A6 6 0 0 1 18 10v3.2l1.4 2.4" />
        <path d="M6 10a6 6 0 0 1 .6-2.6" />
        <path d="M6 13.2V10" />
        <path d="M4.6 15.6A2 2 0 0 0 6 19h12" />
        <path d="M10 21.5a2.2 2.2 0 0 0 4 0" />
      </svg>
      {{ 'alerts.quietHours' | transloco }}
    </span>
  `,
  styles: `
    .quiet-icon {
      flex: none;
      inline-size: 0.85rem;
      block-size: 0.85rem;
    }
  `,
})
export class QuietHoursBadge {}
