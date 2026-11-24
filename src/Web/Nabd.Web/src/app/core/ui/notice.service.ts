import { Injectable, signal } from '@angular/core';

export type NoticeKind = 'info' | 'success' | 'error';

export interface Notice {
  id: number;
  key: string;
  kind: NoticeKind;
  /** Milliseconds before self-dismiss, or null when the notice must be closed by hand. */
  ttl: number | null;
}

/** A success is transient; a failure or a session notice must stay until dismissed. */
const SUCCESS_TTL_MS = 4000;

/** A burst of mutations must not paper over the screen. */
const MAX_VISIBLE = 5;

interface Deadline {
  handle: ReturnType<typeof setTimeout> | null;
  deadlineAt: number;
  remaining: number;
}

/**
 * One non-blocking surface for every answer the user must feel (spec 01 "Error
 * surfaces", spec 02 A13): a mutation that succeeds says so, a refusal stays on
 * screen, a session event stays until dismissed. No new dependency — the
 * surface, the tokens and the RTL are ours.
 *
 * A success can be held still (hover/focus) without lying: the countdown stops
 * with the progress bar, and resumes with the time that was left.
 */
@Injectable({ providedIn: 'root' })
export class NoticeService {
  private nextId = 0;
  private readonly items = signal<Notice[]>([]);
  private readonly clocks = new Map<number, Deadline>();

  readonly notices = this.items.asReadonly();

  show(key: string, kind: NoticeKind = 'info'): void {
    const ttl = kind === 'success' ? SUCCESS_TTL_MS : null;
    const id = ++this.nextId;

    this.items.update((current) => {
      const next = [{ id, key, kind, ttl }, ...current];
      // the newest reads first; the oldest beyond the cap leaves with its clock
      for (const dropped of next.slice(MAX_VISIBLE)) this.forget(dropped.id);
      return next.slice(0, MAX_VISIBLE);
    });

    if (ttl !== null) this.arm(id, ttl);
  }

  /** Stops the clock without removing the notice (pointer or keyboard is on it). */
  hold(id: number): void {
    const clock = this.clocks.get(id);
    if (!clock || clock.handle === null) return;

    clearTimeout(clock.handle);
    clock.handle = null;
    clock.remaining = Math.max(0, clock.deadlineAt - Date.now());
  }

  /** Resumes the clock with the time that was left when it was held. */
  release(id: number): void {
    const clock = this.clocks.get(id);
    if (!clock || clock.handle !== null) return;
    if (clock.remaining <= 0) {
      this.dismiss(id);
      return;
    }
    this.arm(id, clock.remaining);
  }

  dismiss(id: number): void {
    this.forget(id);
    this.items.update((current) => current.filter((notice) => notice.id !== id));
  }

  private arm(id: number, ms: number): void {
    const clock: Deadline = {
      handle: setTimeout(() => this.dismiss(id), ms),
      deadlineAt: Date.now() + ms,
      remaining: ms,
    };
    this.clocks.set(id, clock);
  }

  private forget(id: number): void {
    const clock = this.clocks.get(id);
    if (clock?.handle != null) clearTimeout(clock.handle);
    this.clocks.delete(id);
  }
}
