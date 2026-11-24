import {
  Component,
  ElementRef,
  OnDestroy,
  TemplateRef,
  ViewContainerRef,
  computed,
  forwardRef,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { ControlValueAccessor, NG_VALUE_ACCESSOR } from '@angular/forms';
import { Overlay, OverlayRef } from '@angular/cdk/overlay';
import { TemplatePortal } from '@angular/cdk/portal';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

/** One row of a select. A literal label wins over a translation key. */
export interface SelectOption {
  value: string;
  /** Already-localized text (a template or channel name, for example). */
  label?: string;
  /** A Transloco key; the component re-renders it when the language changes. */
  labelKey?: string;
  disabled?: boolean;
}

let nextSelectId = 0;

/**
 * A select that belongs to this design (spec 02 A15): the closed control is a
 * drawn trigger, the list is a real `role="listbox"` in an overlay, and the
 * control underneath is a `ControlValueAccessor` — so `formControlName`,
 * `required` and `setValue` keep working.
 *
 * The keyboard contract is the native one: ArrowDown/Up move, Home/End jump,
 * typeahead finds, Enter/Space choose, Escape leaves without changing, Tab
 * leaves. Focus never leaves the trigger; `aria-activedescendant` speaks.
 */
@Component({
  selector: 'app-select',
  imports: [TranslocoPipe],
  providers: [
    {
      provide: NG_VALUE_ACCESSOR,
      useExisting: forwardRef(() => SelectComponent),
      multi: true,
    },
  ],
  template: `
    <button
      #trigger
      class="ui-select-trigger"
      type="button"
      role="combobox"
      aria-haspopup="listbox"
      [attr.aria-expanded]="open()"
      [attr.aria-controls]="open() ? listId : null"
      [attr.aria-activedescendant]="activeOptionId()"
      [attr.aria-label]="ariaLabel()"
      [attr.aria-required]="required() ? 'true' : null"
      [attr.aria-disabled]="unavailable() ? 'true' : null"
      [class.is-busy]="busy()"
      [disabled]="isDisabled()"
      (click)="toggle()"
      (keydown)="onKeydown($event)"
    >
      <span class="ui-select-value" [class.is-placeholder]="!selectedOption()">
        @if (selectedOption(); as option) {
          @if (option.labelKey) {
            {{ option.labelKey | transloco }}
          } @else {
            {{ option.label }}
          }
        } @else {
          {{ placeholderKey() | transloco }}
        }
      </span>
      <svg class="ui-select-caret" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
        <path d="m7 10 5 5 5-5" />
      </svg>
    </button>

    <ng-template #panel>
      <div
        class="ui-select-panel"
        role="listbox"
        [id]="listId"
        [attr.aria-label]="ariaLabel()"
        [style.min-inline-size.px]="panelWidth()"
        (mousedown)="$event.preventDefault()"
      >
        @for (option of options(); track option.value; let index = $index) {
          <div
            class="ui-select-option"
            role="option"
            [id]="optionId(index)"
            [class.is-active]="index === activeIndex()"
            [class.is-selected]="option.value === selected()"
            [attr.aria-selected]="option.value === selected()"
            [attr.aria-disabled]="option.disabled ? 'true' : null"
            (mouseenter)="setActive(index)"
            (click)="choose(option)"
          >
            <span class="ui-select-option-label">
              @if (option.labelKey) {
                {{ option.labelKey | transloco }}
              } @else {
                {{ option.label }}
              }
            </span>
            @if (option.value === selected()) {
              <svg class="ui-select-tick" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                <path d="m5 12.5 4.5 4.5L19 7" />
              </svg>
            }
          </div>
        } @empty {
          <div class="ui-select-empty" role="option" aria-disabled="true" aria-selected="false">
            {{ 'common.noOptions' | transloco }}
          </div>
        }
      </div>
    </ng-template>
  `,
  styles: `
    :host {
      display: block;
      min-inline-size: 0;
    }

    .ui-select-trigger {
      display: flex;
      inline-size: 100%;
      align-items: center;
      justify-content: space-between;
      gap: 0.5rem;
      min-block-size: 2.65rem;
      padding-block: 0.55rem;
      padding-inline: 0.75rem;
      border: 1px solid var(--app-border-strong);
      border-radius: var(--app-radius-sm);
      background: var(--app-surface);
      color: var(--app-text);
      font: inherit;
      text-align: start;
      cursor: pointer;
      transition:
        border-color var(--app-duration) var(--app-easing),
        box-shadow var(--app-duration) var(--app-easing),
        background-color var(--app-duration) var(--app-easing);
    }

    .ui-select-trigger:hover:not(:disabled) {
      border-color: var(--app-brand);
    }

    .ui-select-trigger[aria-expanded='true'] {
      border-color: var(--app-brand);
      box-shadow: var(--app-ring);
    }

    .ui-select-trigger:focus-visible {
      outline: none;
      border-color: var(--app-brand);
      box-shadow: var(--app-ring);
    }

    .ui-select-trigger.is-busy {
      cursor: progress;
      opacity: 0.75;
    }

    .ui-select-trigger:disabled {
      background: var(--app-surface-muted);
      color: var(--app-text-muted);
      cursor: not-allowed;
      opacity: 0.7;
    }

    .ui-select-value {
      min-inline-size: 0;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    .ui-select-value.is-placeholder {
      color: var(--app-text-muted);
    }

    .ui-select-caret {
      flex: none;
      inline-size: 1.05rem;
      block-size: 1.05rem;
      color: var(--app-text-muted);
      transition: transform var(--app-duration) var(--app-easing);
    }

    .ui-select-trigger[aria-expanded='true'] .ui-select-caret {
      transform: rotate(180deg);
    }

    // a table row does not need a form field's height
    :host-context(.ui-table) .ui-select-trigger {
      min-block-size: 2.1rem;
      padding-block: 0.3rem;
      font-size: 0.875rem;
    }

    .ui-select-panel {
      min-inline-size: 8rem;
      max-inline-size: min(22rem, calc(100vw - 1rem));
      max-block-size: min(19rem, 60vh);
      overflow-y: auto;
      padding: 0.3rem;
      border: 1px solid var(--app-border);
      border-radius: var(--app-radius-md);
      background: var(--app-surface);
      color: var(--app-text);
      box-shadow: var(--app-shadow-lg);
      animation: select-in 120ms var(--app-easing);
    }

    .ui-select-option {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 0.6rem;
      padding-block: 0.5rem;
      padding-inline: 0.6rem;
      border-radius: var(--app-radius-sm);
      font-size: 0.875rem;
      line-height: 1.35;
      cursor: pointer;
      transition: background-color 90ms linear, color 90ms linear;
    }

    .ui-select-option-label {
      min-inline-size: 0;
      overflow-wrap: anywhere;
    }

    .ui-select-option.is-active {
      background: var(--app-brand-soft);
    }

    .ui-select-option.is-selected {
      color: var(--app-brand-strong);
      font-weight: 600;
    }

    .ui-select-option[aria-disabled='true'] {
      color: var(--app-text-muted);
      cursor: not-allowed;
      opacity: 0.6;
    }

    .ui-select-tick {
      flex: none;
      inline-size: 1rem;
      block-size: 1rem;
      color: var(--app-brand);
    }

    .ui-select-empty {
      margin: 0;
      padding-block: 0.6rem;
      padding-inline: 0.65rem;
      color: var(--app-text-muted);
      font-size: 0.85rem;
    }

    @keyframes select-in {
      from {
        opacity: 0;
        transform: translateY(-3px);
      }

      to {
        opacity: 1;
        transform: translateY(0);
      }
    }

    @media (prefers-reduced-motion: reduce) {
      .ui-select-trigger,
      .ui-select-caret,
      .ui-select-option {
        transition: none;
      }

      .ui-select-panel {
        animation: none;
      }
    }
  `,
})
export class SelectComponent implements ControlValueAccessor, OnDestroy {
  private readonly overlay = inject(Overlay);
  private readonly transloco = inject(TranslocoService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly viewContainerRef = inject(ViewContainerRef);
  private readonly trigger = viewChild.required<ElementRef<HTMLButtonElement>>('trigger');
  private readonly panel = viewChild.required<TemplateRef<unknown>>('panel');

  readonly options = input.required<readonly SelectOption[]>();
  readonly placeholderKey = input('common.select');
  readonly ariaLabel = input<string | null>(null);
  readonly disabled = input(false);
  /** Mirrors the form control's required validator for screen readers. */
  readonly required = input(false);
  /**
   * “Saving, do not pick again.” Unlike `disabled`, this keeps the trigger
   * focusable and announces itself as `aria-disabled` — a native disabled
   * control would throw a keyboard user's focus back to the top of the page.
   */
  readonly busy = input(false);
  /** Controlled value for table cells and other non-form uses. */
  readonly value = input<string | null>(null);
  readonly valueChange = output<string>();

  readonly listId = `ui-select-${++nextSelectId}`;
  readonly open = signal(false);
  readonly panelWidth = signal<number | null>(null);
  readonly activeIndex = signal(-1);

  private readonly cvaDisabled = signal(false);
  private readonly written = signal<string | null>(null);
  private readonly formBound = signal(false);
  private overlayRef: OverlayRef | null = null;
  private outsideSubscription: { unsubscribe(): void } | null = null;
  private typeBuffer = '';
  private typeTimer: ReturnType<typeof setTimeout> | null = null;

  private onChange: ((value: string) => void) | null = null;
  private onTouched: (() => void) | null = null;

  readonly isDisabled = computed(() => this.disabled() || this.cvaDisabled());
  readonly unavailable = computed(() => this.isDisabled() || this.busy());
  readonly selected = computed(() =>
    this.formBound() ? this.written() : this.value(),
  );
  readonly selectedOption = computed(
    () => this.options().find((option) => option.value === this.selected()) ?? null,
  );
  readonly activeOptionId = computed(() => {
    const index = this.activeIndex();
    return this.open() && index >= 0 && index < this.options().length
      ? this.optionId(index)
      : null;
  });

  writeValue(value: unknown): void {
    this.formBound.set(true);
    this.written.set(value == null ? null : String(value));
  }

  registerOnChange(fn: (value: string) => void): void {
    this.onChange = fn;
  }

  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }

  setDisabledState(isDisabled: boolean): void {
    this.cvaDisabled.set(isDisabled);
    if (isDisabled) this.close(false);
  }

  /** Lets a dialog put its cursor on the first field when that field is this control. */
  focus(): void {
    this.trigger().nativeElement.focus();
  }

  toggle(): void {
    if (this.unavailable()) return;
    if (this.open()) this.close(true);
    else this.openPanel('selected');
  }

  openPanel(start: 'selected' | 'first' | 'last' = 'selected'): void {
    if (this.unavailable() || this.open()) return;

    const selected = this.selectedIndex();
    const index =
      start === 'first'
        ? this.firstEnabled()
        : start === 'last'
          ? this.lastEnabled()
          : selected >= 0
            ? selected
            : this.firstEnabled();

    this.activeIndex.set(index);
    this.open.set(true);
    this.panelWidth.set(this.host.nativeElement.getBoundingClientRect().width || null);

    const positionStrategy = this.overlay
      .position()
      .flexibleConnectedTo(this.host.nativeElement)
      .withPositions([
        { originX: 'start', originY: 'bottom', overlayX: 'start', overlayY: 'top', offsetY: 4 },
        { originX: 'start', originY: 'top', overlayX: 'start', overlayY: 'bottom', offsetY: -4 },
      ])
      .withPush(true)
      .withViewportMargin(8)
      .withFlexibleDimensions(false);

    const ref = this.overlay.create({
      positionStrategy,
      scrollStrategy: this.overlay.scrollStrategies.reposition(),
      hasBackdrop: false,
      // read the page's current direction: CDK's root Directionality is born
      // once, and this app flips <html dir> when the language changes
      direction: this.pageDirection(),
    });

    this.overlayRef = ref;
    ref.attach(new TemplatePortal(this.panel(), this.viewContainerRef));
    this.outsideSubscription = ref.outsidePointerEvents().subscribe((event) => {
      // the trigger's own click toggles; do not close twice under it
      if (this.host.nativeElement.contains(event.target as Node)) return;
      this.close(false);
    });

    this.trigger().nativeElement.focus();
    this.scrollActiveIntoView();
  }

  close(focusTrigger = true): void {
    if (!this.open()) return;

    this.open.set(false);
    this.activeIndex.set(-1);
    this.outsideSubscription?.unsubscribe();
    this.outsideSubscription = null;
    this.overlayRef?.dispose();
    this.overlayRef = null;

    this.onTouched?.();
    if (focusTrigger) this.trigger().nativeElement.focus();
  }

  choose(option: SelectOption): void {
    if (option.disabled) return;

    // the display is controlled by the form value (or the [value] input); the
    // write is remembered so a form-bound control paints its choice at once
    this.written.set(option.value);
    this.onChange?.(option.value);
    this.valueChange.emit(option.value);
    this.close(true);
  }

  setActive(index: number): void {
    if (index >= 0 && index < this.options().length) this.activeIndex.set(index);
  }

  onKeydown(event: KeyboardEvent): void {
    if (this.unavailable()) return;

    const key = event.key;

    if (!this.open()) {
      if (key === 'ArrowDown') {
        event.preventDefault();
        this.openPanel('selected');
        return;
      }
      if (key === 'ArrowUp') {
        event.preventDefault();
        this.openPanel(this.selectedIndex() >= 0 ? 'selected' : 'last');
        if (this.selectedIndex() >= 0) this.move(-1);
        return;
      }
      if (key === 'Enter' || key === ' ') {
        event.preventDefault();
        this.openPanel('selected');
        return;
      }
      if (key === 'Home') {
        event.preventDefault();
        this.openPanel('first');
        return;
      }
      if (key === 'End') {
        event.preventDefault();
        this.openPanel('last');
        return;
      }
      if (this.isPrintable(event)) {
        event.preventDefault();
        this.openPanel('selected');
        this.typeahead(key);
      }
      return;
    }

    switch (key) {
      case 'ArrowDown':
        event.preventDefault();
        this.move(1);
        break;
      case 'ArrowUp':
        event.preventDefault();
        this.move(-1);
        break;
      case 'Home':
        event.preventDefault();
        this.setActive(this.firstEnabled());
        this.scrollActiveIntoView();
        break;
      case 'End':
        event.preventDefault();
        this.setActive(this.lastEnabled());
        this.scrollActiveIntoView();
        break;
      case 'Enter':
      case ' ':
        event.preventDefault();
        this.chooseActive();
        break;
      case 'Escape':
        event.preventDefault();
        // a select inside a dialog must not close the dialog underneath it
        event.stopPropagation();
        this.close(true);
        break;
      case 'Tab':
        this.close(false);
        break;
      default:
        if (this.isPrintable(event)) {
          event.preventDefault();
          this.typeahead(key);
        }
    }
  }

  optionId(index: number): string {
    return `${this.listId}-option-${index}`;
  }

  private pageDirection(): 'rtl' | 'ltr' {
    return document.documentElement.getAttribute('dir') === 'rtl' ? 'rtl' : 'ltr';
  }

  private selectedIndex(): number {
    return this.options().findIndex((option) => option.value === this.selected());
  }

  private firstEnabled(): number {
    return this.options().findIndex((option) => !option.disabled);
  }

  private lastEnabled(): number {
    for (let index = this.options().length - 1; index >= 0; index--) {
      if (!this.options()[index].disabled) return index;
    }
    return -1;
  }

  private move(delta: 1 | -1): void {
    const options = this.options();
    let index = this.activeIndex();
    for (let step = 0; step < options.length; step++) {
      index += delta;
      if (index < 0 || index >= options.length) return;
      if (!options[index].disabled) {
        this.setActive(index);
        this.scrollActiveIntoView();
        return;
      }
    }
  }

  private chooseActive(): void {
    const option = this.options()[this.activeIndex()];
    if (option && !option.disabled) this.choose(option);
  }

  private isPrintable(event: KeyboardEvent): boolean {
    return (
      event.key.length === 1 &&
      event.key !== ' ' &&
      !event.ctrlKey &&
      !event.metaKey &&
      !event.altKey
    );
  }

  private typeahead(key: string): void {
    this.typeBuffer += key.toLocaleLowerCase();
    if (this.typeTimer) clearTimeout(this.typeTimer);
    this.typeTimer = setTimeout(() => {
      this.typeBuffer = '';
      this.typeTimer = null;
    }, 600);

    const options = this.options();
    const start = this.activeIndex() + 1;
    for (let step = 0; step < options.length; step++) {
      const index = (start + step) % options.length;
      const option = options[index];
      if (
        !option.disabled &&
        this.labelOf(option).toLocaleLowerCase().startsWith(this.typeBuffer)
      ) {
        this.setActive(index);
        this.scrollActiveIntoView();
        return;
      }
    }
  }

  private labelOf(option: SelectOption): string {
    if (option.labelKey) return this.transloco.translate(option.labelKey);
    return option.label ?? option.value;
  }

  private scrollActiveIntoView(): void {
    const id = this.activeOptionId();
    const pane = this.overlayRef?.overlayElement;
    if (!id || !pane) return;

    const element = pane.querySelector<HTMLElement>(`[id="${id}"]`);
    // jsdom has no layout; a browser scrolls the highlighted row into view
    if (typeof element?.scrollIntoView === 'function') {
      element.scrollIntoView({ block: 'nearest' });
    }
  }

  ngOnDestroy(): void {
    this.close(false);
    if (this.typeTimer) clearTimeout(this.typeTimer);
  }
}
