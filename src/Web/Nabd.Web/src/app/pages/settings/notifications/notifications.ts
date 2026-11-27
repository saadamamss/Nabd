import { Component, computed, inject, OnDestroy, OnInit, signal } from '@angular/core';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { describeApiError } from '../../../core/api/api-error';
import { TelegramApi, TelegramState, TelegramTicket } from '../../../core/api/telegram.service';
import { FeedbackUi } from '../../../core/ui/feedback-ui';
import { NoticeService } from '../../../core/ui/notice.service';
import { SettingsTabs } from '../settings-tabs';

/** N3: the link completes in Telegram, so the page looks every three seconds. */
const POLL_MS = 3000;

/** The countdown moves once a second; it is the same clock that stops the poll. */
const TICK_MS = 1000;

/** mm:ss, always two digits per side, so the block never reflows. */
function formatCountdown(ms: number): string {
  const total = Math.ceil(ms / 1000);
  const minutes = Math.floor(total / 60);
  const seconds = total % 60;
  return `${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}`;
}

/**
 * `/settings/notifications` (spec 08, FR-38): the user's own Telegram channel,
 * plus one honest read-only line about email.
 *
 * The code is a one-time credential (N7): it lives in this component's state
 * only, and it leaves the DOM when the dialog closes or the window ends.
 * While the dialog is open the page watches the server (N3/N10) — the link is
 * completed in Telegram, so nothing here may claim it before the server says
 * `linked`. Severities and unlink are S3.
 */
@Component({
  selector: 'app-notifications',
  imports: [TranslocoPipe, SettingsTabs],
  templateUrl: './notifications.html',
  styleUrl: './notifications.scss',
})
export class NotificationsPage implements OnInit, OnDestroy {
  private readonly api = inject(TelegramApi);
  private readonly transloco = inject(TranslocoService);
  private readonly feedback = inject(FeedbackUi);
  private readonly notices = inject(NoticeService);

  readonly state = signal<TelegramState | null>(null);
  readonly loading = signal(true);
  readonly problemKey = signal<string | null>(null);

  /** The dialog is open; the ticket is its live code (null once expired). */
  readonly linkOpen = signal(false);
  readonly ticket = signal<TelegramTicket | null>(null);
  readonly opening = signal(false);
  readonly ticketProblem = signal<string | null>(null);
  readonly copied = signal(false);

  /** Ticked once a second while the dialog is open; the countdown reads it. */
  private readonly now = signal(Date.now());

  private pollClock: ReturnType<typeof setInterval> | null = null;
  private tickClock: ReturnType<typeof setInterval> | null = null;
  /** N10: one poll in flight at a time, so "every 3 s" stays a true count. */
  private polling = false;

  /**
   * The server's window minus the browser clock. `ExpiresAt` is the DB clock
   * (C5); a browser skewed by more than the ten-minute window would see an
   * expired code — recorded, not hidden.
   */
  readonly remainingMs = computed(() => {
    const ticket = this.ticket();
    if (!ticket) return 0;
    const expiresAt = Date.parse(ticket.expiresAt);
    if (Number.isNaN(expiresAt)) return 0;
    return Math.max(0, expiresAt - this.now());
  });

  readonly countdown = computed(() => formatCountdown(this.remainingMs()));

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

  ngOnDestroy(): void {
    this.stopWatching();
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

  /** Also "get a new code": the door replaces the previous ticket (C5). */
  openLink(): void {
    if (this.opening()) return;
    this.opening.set(true);
    this.ticketProblem.set(null);
    this.copied.set(false);
    this.api.link().subscribe({
      next: (ticket) => {
        this.opening.set(false);
        this.ticket.set(ticket);
        this.linkOpen.set(true);
        this.now.set(Date.now());
        this.startWatching();
      },
      error: (error: unknown) => {
        this.ticketProblem.set(describeApiError(error).key);
        this.opening.set(false);
      },
    });
  }

  closeLink(): void {
    this.stopWatching();
    this.linkOpen.set(false);
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

  private startWatching(): void {
    this.stopWatching();
    this.pollClock = setInterval(() => this.checkLink(), POLL_MS);
    this.tickClock = setInterval(() => this.tick(), TICK_MS);
  }

  private stopWatching(): void {
    if (this.pollClock !== null) {
      clearInterval(this.pollClock);
      this.pollClock = null;
    }
    if (this.tickClock !== null) {
      clearInterval(this.tickClock);
      this.tickClock = null;
    }
  }

  private tick(): void {
    this.now.set(Date.now());
    if (this.ticket() && this.remainingMs() === 0) this.expire();
  }

  /** N7: the window ends, the code leaves the page with its clock. */
  private expire(): void {
    this.stopWatching();
    this.ticket.set(null);
    this.copied.set(false);
  }

  private checkLink(): void {
    if (this.polling) return;
    this.polling = true;
    this.api.get().subscribe({
      next: (next) => {
        this.polling = false;
        // a late answer after close must not close again or toast twice
        if (!this.linkOpen() || !next.linked) return;
        this.state.set(next);
        this.closeLink();
        this.notices.show('settings.linkSuccess', 'success');
      },
      error: () => {
        // a failed observation claims nothing; the next beat asks again
        this.polling = false;
      },
    });
  }
}
