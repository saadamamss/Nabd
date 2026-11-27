import { Injectable, signal } from '@angular/core';

/**
 * P5 spec 08 (C8): one shared trigger for the shell's feedback dialog.
 *
 * The dialog and its state belong to `FeedbackButton` (spec 06), mounted once
 * on the shell. A screen that wants to point at it may only ask — it cannot
 * own or duplicate the dialog. The counter is the request: every `open()`
 * moves it, and the button opens on a change.
 */
@Injectable({ providedIn: 'root' })
export class FeedbackUi {
  private readonly openRequests = signal(0);

  /** How many times a screen asked for the dialog; the button watches this. */
  readonly requests = this.openRequests.asReadonly();

  open(): void {
    this.openRequests.update((count) => count + 1);
  }
}
