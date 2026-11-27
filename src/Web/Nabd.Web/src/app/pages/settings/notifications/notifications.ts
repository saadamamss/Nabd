import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { describeApiError } from '../../../core/api/api-error';
import { TelegramApi, TelegramState, TelegramTicket } from '../../../core/api/telegram.service';
import { FeedbackUi } from '../../../core/ui/feedback-ui';
import { SettingsTabs } from '../settings-tabs';

/**
 * `/settings/notifications` (spec 08, FR-38): the user's own Telegram channel,
 * plus one honest read-only line about email.
 *
 * S1 draws the page and the link ticket: the code is shown once, it lives in
 * this component's state only, and nothing is claimed before the door answers.
 * Observation (polling/countdown), severities and unlink are later slices —
 * this page half renders exactly what the server said and promises nothing else.
 */
@Component({
  selector: 'app-notifications',
  imports: [TranslocoPipe, SettingsTabs],
  templateUrl: './notifications.html',
  styleUrl: './notifications.scss',
})
export class NotificationsPage implements OnInit {
  private readonly api = inject(TelegramApi);
  private readonly transloco = inject(TranslocoService);
  private readonly feedback = inject(FeedbackUi);

  readonly state = signal<TelegramState | null>(null);
  readonly loading = signal(true);
  readonly problemKey = signal<string | null>(null);

  /** The one-time ticket; the plaintext code never leaves this signal (N7). */
  readonly ticket = signal<TelegramTicket | null>(null);
  readonly opening = signal(false);
  readonly ticketProblem = signal<string | null>(null);
  readonly copied = signal(false);

  /** The server's `verifiedAt` in the active locale — never a guessed date. */
  readonly verifiedText = computed(() => {
    const at = this.state()?.verifiedAt;
    if (!at) return '';
    return new Intl.DateTimeFormat(this.transloco.activeLang(), {
      dateStyle: 'medium',
      timeStyle: 'short',
    }).format(new Date(at));
  });

  /** `t.me/{bot}?start={code}` only when the server configured a username (N2). */
  readonly deepLink = computed(() => {
    const ticket = this.ticket();
    if (!ticket?.botUsername) return null;
    return `https://t.me/${ticket.botUsername}?start=${encodeURIComponent(ticket.code)}`;
  });

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.problemKey.set(null);
    this.api.get().subscribe({
      next: (state) => {
        this.state.set(state);
        this.loading.set(false);
      },
      error: (error: unknown) => {
        this.problemKey.set(describeApiError(error).key);
        this.loading.set(false);
      },
    });
  }

  openLink(): void {
    if (this.opening()) return;
    this.opening.set(true);
    this.ticketProblem.set(null);
    this.copied.set(false);
    this.api.link().subscribe({
      next: (ticket) => {
        this.ticket.set(ticket);
        this.opening.set(false);
      },
      error: (error: unknown) => {
        this.ticketProblem.set(describeApiError(error).key);
        this.opening.set(false);
      },
    });
  }

  closeLink(): void {
    this.ticket.set(null);
    this.ticketProblem.set(null);
    this.copied.set(false);
  }

  /**
   * C6: the clipboard is optional outside secure contexts. No clipboard means
   * no claim — the code is selectable and stays on screen either way.
   */
  copy(): void {
    const code = this.ticket()?.code;
    const clipboard = navigator.clipboard;
    if (!code || !clipboard) return;
    void clipboard
      .writeText(code)
      .then(() => this.copied.set(true))
      .catch(() => this.copied.set(false));
  }

  openFeedback(): void {
    this.feedback.open();
  }
}
