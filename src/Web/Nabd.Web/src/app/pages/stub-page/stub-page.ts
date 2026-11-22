import { Component, computed, inject } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

/**
 * One honest placeholder for every route whose real page lands in 02–09.
 * The route's `data.page` selects the dictionary pair.
 */
@Component({
  selector: 'app-stub-page',
  imports: [TranslocoPipe],
  template: `
    <section class="page">
      <h1>{{ titleKey() | transloco }}</h1>
      <p>{{ bodyKey() | transloco }}</p>
      <p class="note">{{ 'common.comingSoon' | transloco }}</p>
    </section>
  `,
  styles: `
    .page {
      max-inline-size: 48rem;
    }

    h1 {
      margin-block: 0 0.5rem;
      font-size: 1.5rem;
    }

    p {
      margin-block: 0 0.75rem;
      color: var(--mat-sys-on-surface-variant);
    }

    .note {
      font-size: 0.875rem;
    }
  `,
})
export class StubPage {
  private readonly route = inject(ActivatedRoute);
  private readonly page = this.route.snapshot.data['page'] as string;

  readonly titleKey = computed(() => `pages.${this.page}.title`);
  readonly bodyKey = computed(() => `pages.${this.page}.body`);
}
