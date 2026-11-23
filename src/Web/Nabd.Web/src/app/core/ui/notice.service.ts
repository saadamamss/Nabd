import { Injectable, signal } from '@angular/core';

export interface Notice {
  id: number;
  key: string;
}

/** One non-blocking surface for transport/server failures (spec 01, "Error surfaces"). */
@Injectable({ providedIn: 'root' })
export class NoticeService {
  private nextId = 0;
  private readonly items = signal<Notice[]>([]);

  readonly notices = this.items.asReadonly();

  show(key: string): void {
    this.items.update((current) => [...current, { id: ++this.nextId, key }]);
  }

  dismiss(id: number): void {
    this.items.update((current) => current.filter((notice) => notice.id !== id));
  }
}
