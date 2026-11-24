import { Component, inject, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { NoticeService } from '../../core/ui/notice.service';

/**
 * The answer surface (spec 02 A13): a success carries its own countdown and can
 * be held still by hovering or focusing it; a refusal has no clock and leaves
 * only when the user closes it. Politeness rides each card, not the container,
 * so an error is never queued behind a success.
 */
@Component({
  selector: 'app-notices',
  imports: [TranslocoPipe],
  template: `
    <div class="notices">
      @for (notice of notices.notices(); track notice.id) {
        <div
          class="notice"
          [class.is-success]="notice.kind === 'success'"
          [class.is-error]="notice.kind === 'error'"
          [class.is-held]="held().has(notice.id)"
          [attr.role]="notice.kind === 'error' ? 'alert' : 'status'"
          [attr.aria-live]="notice.kind === 'error' ? 'assertive' : 'polite'"
          (mouseenter)="hold(notice.id)"
          (mouseleave)="release(notice.id)"
          (focusin)="hold(notice.id)"
          (focusout)="release(notice.id)"
        >
          <span class="notice-icon" aria-hidden="true">
            @switch (notice.kind) {
              @case ('success') {
                <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round">
                  <path d="m5 12.5 4.5 4.5L19 7" />
                </svg>
              }
              @case ('error') {
                <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round">
                  <path d="M12 7.5v5.5" />
                  <path d="M12 16.4h.01" />
                  <circle cx="12" cy="12" r="9" stroke-width="1.7" />
                </svg>
              }
              @default {
                <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round">
                  <path d="M12 11v6" />
                  <path d="M12 7.2h.01" />
                  <circle cx="12" cy="12" r="9" stroke-width="1.7" />
                </svg>
              }
            }
          </span>

          <p class="notice-text">{{ notice.key | transloco }}</p>

          <button
            type="button"
            class="notice-dismiss"
            [attr.aria-label]="'common.dismiss' | transloco"
            [attr.title]="'common.dismiss' | transloco"
            (click)="notices.dismiss(notice.id)"
          >
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" aria-hidden="true">
              <path d="m7 7 10 10M17 7 7 17" />
            </svg>
          </button>

          @if (notice.ttl) {
            <span class="notice-meter" [style.--toast-ttl]="notice.ttl + 'ms'" aria-hidden="true"></span>
          }
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
      gap: 0.6rem;
      inline-size: min(23rem, calc(100vw - 2rem));
      pointer-events: none;
    }

    .notice {
      --toast-accent: var(--app-brand);

      position: relative;
      display: flex;
      align-items: flex-start;
      gap: 0.7rem;
      overflow: hidden;
      padding-block: 0.8rem;
      padding-inline: 0.9rem;
      border: 1px solid color-mix(in srgb, var(--toast-accent) 26%, var(--app-border));
      border-radius: 0.9rem;
      background: color-mix(in srgb, var(--app-surface) 92%, transparent);
      backdrop-filter: blur(10px) saturate(1.15);
      color: var(--app-text);
      box-shadow:
        0 18px 42px -22px rgb(2 8 23 / 55%),
        0 2px 6px rgb(2 8 23 / 8%);
      pointer-events: auto;
      animation: notice-in 220ms cubic-bezier(0.2, 0.8, 0.2, 1);
    }

    .notice.is-success {
      --toast-accent: var(--app-healthy);
    }

    .notice.is-error {
      --toast-accent: var(--app-alert);
    }

    .notice-icon {
      display: grid;
      place-items: center;
      flex: none;
      inline-size: 1.75rem;
      block-size: 1.75rem;
      margin-block-start: 0.05rem;
      border-radius: 999px;
      background: color-mix(in srgb, var(--toast-accent) 14%, transparent);
      color: var(--toast-accent);
    }

    .notice-icon svg {
      inline-size: 1.05rem;
      block-size: 1.05rem;
    }

    .notice-text {
      flex: 1;
      margin: 0;
      padding-block-start: 0.15rem;
      font-size: 0.875rem;
      line-height: 1.45;
      overflow-wrap: anywhere;
    }

    .notice-dismiss {
      display: grid;
      place-items: center;
      flex: none;
      inline-size: 1.5rem;
      block-size: 1.5rem;
      padding: 0;
      border: 0;
      border-radius: 999px;
      background: transparent;
      color: var(--app-text-muted);
      opacity: 0.55;
      cursor: pointer;
      transition: opacity 150ms ease, background-color 150ms ease;
    }

    .notice-dismiss:hover {
      background: var(--app-surface-muted);
      color: var(--app-text);
      opacity: 1;
    }

    .notice-dismiss:focus-visible {
      opacity: 1;
      outline: none;
      box-shadow: var(--app-ring);
    }

    .notice-dismiss svg {
      inline-size: 0.85rem;
      block-size: 0.85rem;
    }

    .notice-meter {
      position: absolute;
      inset-block-end: 0;
      inset-inline: 0;
      block-size: 2px;
      background: color-mix(in srgb, var(--toast-accent) 65%, transparent);
      transform-origin: 0 50%;
      animation: notice-meter var(--toast-ttl) linear forwards;
    }

    :dir(rtl) .notice-meter {
      transform-origin: 100% 50%;
    }

    .notice.is-held .notice-meter {
      animation-play-state: paused;
    }

    @keyframes notice-in {
      from {
        opacity: 0;
        transform: translateY(-10px) scale(0.97);
      }

      to {
        opacity: 1;
        transform: translateY(0) scale(1);
      }
    }

    @keyframes notice-meter {
      from {
        transform: scaleX(1);
      }

      to {
        transform: scaleX(0);
      }
    }

    @media (prefers-reduced-motion: reduce) {
      .notice {
        animation: none;
      }
    }
  `,
})
export class NoticesComponent {
  readonly notices = inject(NoticeService);
  readonly held = signal<ReadonlySet<number>>(new Set());

  hold(id: number): void {
    this.notices.hold(id);
    this.held.update((current) => new Set(current).add(id));
  }

  release(id: number): void {
    this.notices.release(id);
    const next = new Set(this.held());
    next.delete(id);
    this.held.set(next);
  }
}
