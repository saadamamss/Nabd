import { Component, computed, effect, ElementRef, inject, signal, viewChild } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { Router } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { describeApiError } from '../../core/api/api-error';
import { FeedbackApi } from '../../core/api/feedback.service';
import { NoticeService } from '../../core/ui/notice.service';
import { FeedbackUi } from '../../core/ui/feedback-ui';

/** The table's ceiling (0030's `feedback_message_len`) — counted in characters. */
const MAX = 2000;

const RETRY_CODE = 'feedback:rate_limited';

interface Problem {
  key: string;
  params?: Record<string, unknown>;
}

/**
 * P5 spec 06 S4 (F7): the in-product feedback button. One floating control on
 * the shell (so it exists on every authenticated route), one dialog with a
 * message and an optional reply address.
 *
 * Two honesty rules shape the code: a refusal NEVER eats the typed text (the
 * dialog stays open and the message stays put), and a 429 says how long to
 * wait — the server's `retryAfterSeconds`, not a guessed number.
 */
@Component({
  selector: 'app-feedback',
  imports: [TranslocoPipe],
  templateUrl: './feedback.html',
  styleUrl: './feedback.scss',
})
export class FeedbackButton {
  private readonly api = inject(FeedbackApi);
  private readonly notices = inject(NoticeService);
  private readonly router = inject(Router);
  private readonly transloco = inject(TranslocoService);

  private readonly box = viewChild<ElementRef<HTMLTextAreaElement>>('box');

  /** P5 spec 08 (C8): another screen may ask for this dialog; the button owns it. */
  private readonly trigger = inject(FeedbackUi);

  constructor() {
    // only a NEW request opens: a re-created button must not replay an old one
    let seen = this.trigger.requests();
    effect(() => {
      const requested = this.trigger.requests();
      if (requested === seen) return;
      seen = requested;
      this.open();
    });
  }

  readonly isOpen = signal(false);
  readonly message = signal('');
  readonly contact = signal('');
  readonly busy = signal(false);
  readonly problem = signal<Problem | null>(null);

  readonly max = MAX;

  /** Characters, not UTF-16 units: the table counts code points (S2's lesson). */
  readonly count = computed(() => Array.from(this.message()).length);

  readonly canSend = computed(
    () => !this.busy() && this.count() > 0 && this.count() <= MAX,
  );

  open(): void {
    this.isOpen.set(true);
    // the dialog is rendered on the next change detection; focus it then
    setTimeout(() => this.box()?.nativeElement.focus(), 0);
  }

  /** Escape/cancel: the text stays, so a closed dialog is never lost work. */
  close(): void {
    this.isOpen.set(false);
  }

  /**
   * The form's own `submit`, not `ngSubmit`: NgForm is not imported here (this
   * form has no form group), so `(ngSubmit)` binds to nothing and the browser
   * runs its native GET to the same URL — a full reload that throws the draft
   * away before the POST is ever made. The event is cancelled first.
   */
  submit(event: Event): void {
    event.preventDefault();
    this.send();
  }

  send(): void {
    if (!this.canSend()) return;

    this.busy.set(true);
    this.problem.set(null);

    this.api
      .send({
        message: this.message().trim(),
        page: this.pathname(),
        locale: this.transloco.getActiveLang(),
        contactEmail: this.contact().trim() || null,
      })
      .subscribe({
        next: () => {
          this.busy.set(false);
          this.isOpen.set(false);
          this.message.set('');
          this.contact.set('');
          this.notices.show('feedback.sent', 'success');
        },
        error: (error: unknown) => {
          this.busy.set(false);
          this.problem.set(this.describe(error));
        },
      });
  }

  /** Reads a textarea/input's value without spreading a form group over one field. */
  value(event: Event): string {
    return (event.target as HTMLTextAreaElement | HTMLInputElement).value;
  }

  private pathname(): string {
    const path = this.router.url.split(/[?#]/)[0];
    return path || '/';
  }

  private describe(error: unknown): Problem {
    const body =
      error instanceof HttpErrorResponse
        ? (error.error as { code?: unknown; retryAfterSeconds?: unknown } | null)
        : null;

    if (body?.code === RETRY_CODE) {
      const seconds =
        typeof body.retryAfterSeconds === 'number' ? body.retryAfterSeconds : 30;
      return { key: 'feedback.retryIn', params: { seconds } };
    }

    return { key: describeApiError(error).key };
  }
}
