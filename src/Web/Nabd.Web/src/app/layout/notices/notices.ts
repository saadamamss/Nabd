import { Component, inject } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { NoticeService } from '../../core/ui/notice.service';

@Component({
  selector: 'app-notices',
  imports: [TranslocoPipe],
  template: `
    <div class="notices" role="status" aria-live="polite">
      @for (notice of notices.notices(); track notice.id) {
        <div class="notice">
          <span class="notice-icon" aria-hidden="true">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round">
              <path d="M12 8v5" />
              <path d="M12 16.5h.01" />
              <path d="M10.3 4.2 2.9 17a2 2 0 0 0 1.7 3h14.8a2 2 0 0 0 1.7-3L13.7 4.2a2 2 0 0 0-3.4 0Z" />
            </svg>
          </span>
          <span class="notice-text">{{ notice.key | transloco }}</span>
          <button
            type="button"
            class="notice-dismiss"
            [attr.aria-label]="'common.dismiss' | transloco"
            [attr.title]="'common.dismiss' | transloco"
            (click)="notices.dismiss(notice.id)"
          >
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true">
              <path d="m6 6 12 12M18 6 6 18" />
            </svg>
          </button>
        </div>
      }
    </div>
  `,
  styles: `
    .notices {
      position: fixed;
      inset-block-start: 1rem;
      inset-inline-end: 1rem;
      z-index: 1000;
      display: flex;
      flex-direction: column;
      gap: 0.5rem;
      max-inline-size: min(24rem, calc(100vw - 2rem));
    }

    .notice {
      display: flex;
      align-items: flex-start;
      gap: 0.75rem;
      padding-block: 0.75rem;
      padding-inline: 0.875rem;
      border: 1px solid var(--app-border);
      border-inline-start: 3px solid var(--app-brand);
      border-radius: var(--app-radius-md);
      background: var(--app-surface);
      color: var(--app-text);
      box-shadow: var(--app-shadow-lg);
      animation: notice-in var(--app-duration) var(--app-easing);
    }

    .notice-icon {
      display: grid;
      place-items: center;
      flex: none;
      inline-size: 1.5rem;
      block-size: 1.5rem;
      color: var(--app-brand);
    }

    .notice-icon svg {
      inline-size: 1.25rem;
      block-size: 1.25rem;
    }

    .notice-text {
      flex: 1;
      font-size: 0.875rem;
      line-height: 1.45;
    }

    .notice-dismiss {
      display: grid;
      place-items: center;
      flex: none;
      inline-size: 1.5rem;
      block-size: 1.5rem;
      padding: 0;
      border: 0;
      border-radius: 0.375rem;
      background: transparent;
      color: var(--app-text-muted);
      cursor: pointer;
    }

    .notice-dismiss:hover {
      background: var(--app-surface-muted);
      color: var(--app-text);
    }

    .notice-dismiss svg {
      inline-size: 0.9rem;
      block-size: 0.9rem;
    }

    @keyframes notice-in {
      from {
        opacity: 0;
        transform: translateY(-6px);
      }

      to {
        opacity: 1;
        transform: translateY(0);
      }
    }
  `,
})
export class NoticesComponent {
  readonly notices = inject(NoticeService);
}
