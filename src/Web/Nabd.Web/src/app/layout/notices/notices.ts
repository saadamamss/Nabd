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
          <span class="notice-text">{{ notice.key | transloco }}</span>
          <button type="button" class="notice-dismiss" (click)="notices.dismiss(notice.id)">
            {{ 'common.dismiss' | transloco }}
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
      align-items: center;
      gap: 0.75rem;
      padding-block: 0.5rem;
      padding-inline: 0.75rem;
      border-radius: 0.5rem;
      background: var(--mat-sys-inverse-surface);
      color: var(--mat-sys-inverse-on-surface);
      box-shadow: var(--mat-sys-level2);
    }

    .notice-text {
      flex: 1;
      font-size: 0.875rem;
    }

    .notice-dismiss {
      border: 0;
      border-radius: 0.25rem;
      padding-block: 0.25rem;
      padding-inline: 0.5rem;
      background: transparent;
      color: inherit;
      font: inherit;
      cursor: pointer;
      text-decoration: underline;
    }
  `,
})
export class NoticesComponent {
  readonly notices = inject(NoticeService);
}
