import { Component, computed, inject } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

/**
 * Designed placeholder for every route whose real page lands in 02–09.
 * Overview previews the future dashboard layout with shapes only — never sample
 * values, because the UI must not show data the server did not send.
 */
@Component({
  selector: 'app-stub-page',
  imports: [TranslocoPipe],
  template: `
    <section class="page">
      <header class="page-head">
        <div class="head-copy">
          <span class="kicker">{{ 'common.workspace' | transloco }}</span>
          <h1>{{ titleKey() | transloco }}</h1>
          <p>{{ bodyKey() | transloco }}</p>
        </div>
        @if (page === 'overview') {
          <span class="chip">
            <span class="chip-dot" aria-hidden="true"></span>
            {{ 'common.preview' | transloco }}
          </span>
        }
      </header>

      @if (page === 'overview') {
        <figure class="preview">
          <div class="stage" aria-hidden="true">
            <div class="mini">
              <div class="mini-rail">
                <span class="mini-mark"></span>
                <span class="mini-link is-on"></span>
                <span class="mini-link"></span>
                <span class="mini-link"></span>
                <span class="mini-link"></span>
              </div>
              <div class="mini-main">
                <div class="mini-head">
                  <span class="mini-title"></span>
                  <span class="mini-chip"></span>
                </div>
                <div class="mini-kpis">
                  <span class="mini-kpi"></span>
                  <span class="mini-kpi"></span>
                  <span class="mini-kpi"></span>
                </div>
                <div class="mini-grid">
                  <span class="mini-card">
                    <span class="mini-status is-healthy"></span>
                    <span class="mini-line"></span>
                    <span class="mini-line is-short"></span>
                  </span>
                  <span class="mini-card">
                    <span class="mini-status is-warning"></span>
                    <span class="mini-line"></span>
                    <span class="mini-line is-short"></span>
                  </span>
                  <span class="mini-card">
                    <span class="mini-status is-alert"></span>
                    <span class="mini-line"></span>
                    <span class="mini-line is-short"></span>
                  </span>
                  <span class="mini-card">
                    <span class="mini-status is-offline"></span>
                    <span class="mini-line"></span>
                    <span class="mini-line is-short"></span>
                  </span>
                </div>
              </div>
            </div>
          </div>
          <figcaption>{{ 'common.comingSoon' | transloco }}</figcaption>
        </figure>
      } @else {
        <div class="callout">
          <span class="callout-icon" aria-hidden="true">
            <svg
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              stroke-width="1.6"
              stroke-linecap="round"
              stroke-linejoin="round"
            >
              <path d="M12 3 3 8l9 5 9-5-9-5Z" />
              <path d="m3 12 9 5 9-5" />
              <path d="m3 16 9 5 9-5" />
            </svg>
          </span>
          <p>{{ 'common.comingSoon' | transloco }}</p>
        </div>
      }
    </section>
  `,
  styles: `
    .page {
      max-inline-size: 76rem;
    }

    .page-head {
      display: flex;
      flex-wrap: wrap;
      align-items: flex-start;
      justify-content: space-between;
      gap: 1.25rem 2rem;
      margin-block-end: clamp(1.75rem, 3.5vw, 2.5rem);
    }

    .kicker {
      display: inline-flex;
      align-items: center;
      gap: 0.55rem;
      color: var(--app-brand);
      font-size: 0.8125rem;
      font-weight: 600;
    }

    .kicker::before {
      content: '';
      inline-size: 1.1rem;
      block-size: 2px;
      border-radius: 999px;
      background: currentColor;
    }

    .head-copy h1 {
      margin-block: 0.5rem 0.625rem;
      font-size: clamp(1.75rem, 3.2vw, 2.25rem);
      letter-spacing: -0.015em;
    }

    .head-copy p {
      max-inline-size: 44rem;
      color: var(--app-text-muted);
      font-size: 1.0625rem;
      line-height: 1.55;
    }

    .chip {
      display: inline-flex;
      align-items: center;
      gap: 0.45rem;
      padding-block: 0.45rem;
      padding-inline: 0.8rem;
      border: 1px solid color-mix(in srgb, var(--app-brand) 22%, transparent);
      border-radius: 999px;
      background: var(--app-brand-soft);
      color: var(--app-brand);
      font-size: 0.75rem;
      font-weight: 600;
      white-space: nowrap;
    }

    .chip-dot {
      inline-size: 0.4rem;
      block-size: 0.4rem;
      border-radius: 50%;
      background: currentColor;
    }

    .preview {
      margin: 0;
    }

    .stage {
      position: relative;
      padding-block: clamp(1rem, 2.5vw, 1.75rem) 0;
      padding-inline: clamp(1rem, 2.5vw, 1.75rem);
      overflow: hidden;
      border: 1px solid var(--app-border);
      border-radius: var(--app-radius-lg);
      background:
        radial-gradient(90% 120% at 100% 0%, var(--app-brand-soft), transparent 62%),
        var(--app-surface);
      box-shadow: var(--app-shadow-sm);
    }

    .mini {
      position: relative;
      display: grid;
      grid-template-columns: 4.75rem minmax(0, 1fr);
      gap: 0.55rem;
      block-size: clamp(13rem, 24vw, 16.5rem);
      overflow: hidden;
      border: 1px solid var(--app-border);
      border-block-end: 0;
      border-radius: 0.9rem 0.9rem 0 0;
      background: var(--app-canvas);
      box-shadow: 0 30px 60px -40px rgb(8 16 32 / 45%);
    }

    .mini-rail {
      display: flex;
      flex-direction: column;
      gap: 0.55rem;
      padding: 0.6rem 0.5rem;
      border-inline-end: 1px solid var(--app-border);
      background: var(--app-surface);
    }

    .mini-mark {
      margin-block-end: 0.3rem;
      inline-size: 1.15rem;
      block-size: 1.15rem;
      border-radius: 0.35rem;
      background: linear-gradient(140deg, #1d4ed8, #0ea5e9);
    }

    .mini-link {
      block-size: 0.3rem;
      border-radius: 999px;
      background: var(--app-border);
    }

    .mini-link:nth-child(3) {
      inline-size: 82%;
    }

    .mini-link:nth-child(4) {
      inline-size: 90%;
    }

    .mini-link:nth-child(5) {
      inline-size: 68%;
    }

    .mini-link.is-on {
      background: var(--app-brand);
      opacity: 0.5;
    }

    .mini-main {
      display: flex;
      flex-direction: column;
      gap: 0.55rem;
      min-inline-size: 0;
      padding: 0.65rem 0.7rem 0;
    }

    .mini-head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 0.5rem;
    }

    .mini-title {
      inline-size: 32%;
      block-size: 0.45rem;
      border-radius: 999px;
      background: var(--app-border-strong);
    }

    .mini-chip {
      inline-size: 15%;
      block-size: 0.85rem;
      border-radius: 999px;
      background: var(--app-brand-soft);
    }

    .mini-kpis {
      display: grid;
      grid-template-columns: repeat(3, minmax(0, 1fr));
      gap: 0.45rem;
    }

    .mini-kpi {
      block-size: clamp(1.8rem, 3.5vw, 2.4rem);
      border: 1px solid var(--app-border);
      border-radius: 0.45rem;
      background: linear-gradient(150deg, var(--app-surface), var(--app-surface-muted));
    }

    .mini-grid {
      display: grid;
      flex: 1;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 0.45rem;
    }

    .mini-card {
      position: relative;
      display: flex;
      flex-direction: column;
      gap: 0.3rem;
      padding: 0.5rem;
      border: 1px solid var(--app-border);
      border-radius: 0.5rem;
      background: var(--app-surface);
    }

    .mini-status {
      position: absolute;
      inset-block-start: 0.5rem;
      inset-inline-end: 0.5rem;
      inline-size: 0.35rem;
      block-size: 0.35rem;
      border-radius: 50%;
    }

    .mini-status.is-healthy {
      background: var(--app-healthy);
    }

    .mini-status.is-warning {
      background: var(--app-warning);
    }

    .mini-status.is-alert {
      background: var(--app-alert);
    }

    .mini-status.is-offline {
      background: var(--app-offline);
    }

    .mini-line {
      margin-block-start: 0.35rem;
      inline-size: 62%;
      block-size: 0.25rem;
      border-radius: 999px;
      background: var(--app-border);
    }

    .mini-line.is-short {
      margin-block-start: 0;
      inline-size: 42%;
      background: var(--app-surface-muted);
    }

    .preview figcaption {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      margin-block-start: 0.9rem;
      color: var(--app-text-muted);
      font-size: 0.8125rem;
    }

    .preview figcaption::before {
      content: '';
      inline-size: 0.4rem;
      block-size: 0.4rem;
      border-radius: 50%;
      background: var(--app-brand);
    }

    .callout {
      display: flex;
      align-items: center;
      gap: 1rem;
      max-inline-size: 42rem;
      padding: clamp(1.35rem, 3.5vw, 2rem);
      border: 1px solid var(--app-border);
      border-radius: var(--app-radius-lg);
      background: linear-gradient(160deg, var(--app-surface), var(--app-surface-muted));
      box-shadow: var(--app-shadow-sm);
    }

    .callout-icon {
      display: grid;
      place-items: center;
      flex: none;
      inline-size: 2.75rem;
      block-size: 2.75rem;
      border-radius: var(--app-radius-md);
      background: var(--app-brand-soft);
      color: var(--app-brand);
    }

    .callout-icon svg {
      inline-size: 1.35rem;
      block-size: 1.35rem;
    }

    .callout p {
      color: var(--app-text-muted);
    }
  `,
})
export class StubPage {
  private readonly route = inject(ActivatedRoute);
  readonly page = this.route.snapshot.data['page'] as string;

  readonly titleKey = computed(() => `pages.${this.page}.title`);
  readonly bodyKey = computed(() => `pages.${this.page}.body`);
}
