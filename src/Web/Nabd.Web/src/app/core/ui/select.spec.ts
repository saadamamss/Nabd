import { Component, inject, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslocoService } from '@jsverse/transloco';
import { provideTestTransloco } from '../../testing/test-transloco';
import { SelectComponent, SelectOption } from './select';

@Component({
  imports: [SelectComponent, ReactiveFormsModule],
  template: `
    <form [formGroup]="form">
      <app-select formControlName="role" [options]="options" [required]="true" ariaLabel="Role" />
    </form>
    <app-select
      class="plain"
      [options]="options"
      [value]="plain()"
      (valueChange)="plain.set($event)"
      ariaLabel="Plain"
    />
    <app-select class="off" [options]="options" [disabled]="true" ariaLabel="Off" />
    <app-select class="bn" [options]="options" [busy]="busy()" ariaLabel="Saving" />
  `,
})
class Host {
  private readonly fb = inject(FormBuilder);

  readonly options: SelectOption[] = [
    { value: 'viewer', labelKey: 'roles.viewer' },
    { value: 'tenant_admin', labelKey: 'roles.tenantAdmin' },
    { value: 'disabled', label: 'Disabled choice', disabled: true },
  ];

  readonly form = this.fb.group({ role: ['viewer', Validators.required] });
  readonly plain = signal('tenant_admin');
  readonly busy = signal(false);
}

function setup(): {
  fixture: ComponentFixture<Host>;
  host: Host;
} {
  TestBed.configureTestingModule({
    imports: [Host],
    providers: [...provideTestTransloco()],
  });

  const fixture = TestBed.createComponent(Host);
  fixture.detectChanges();

  return { fixture, host: fixture.componentInstance };
}

function triggerOf(fixture: ComponentFixture<Host>, selector = 'form app-select'): HTMLButtonElement {
  return (fixture.nativeElement as HTMLElement).querySelector(
    `${selector} .ui-select-trigger`,
  ) as HTMLButtonElement;
}

function panel(): HTMLElement | null {
  return document.querySelector('.ui-select-panel');
}

function options(): HTMLElement[] {
  return [...document.querySelectorAll<HTMLElement>('.ui-select-option')];
}

function key(trigger: HTMLElement, value: string): void {
  trigger.dispatchEvent(new KeyboardEvent('keydown', { key: value, bubbles: true }));
}

describe('SelectComponent', () => {
  it('shows the form control’s real value (not the first option) and writes a pick back', () => {
    const { fixture, host } = setup();
    const trigger = triggerOf(fixture);

    expect(trigger.textContent?.trim()).toBe('Viewer');
    expect(trigger.getAttribute('role')).toBe('combobox');
    expect(trigger.getAttribute('aria-expanded')).toBe('false');

    trigger.click();
    fixture.detectChanges();

    expect(trigger.getAttribute('aria-expanded')).toBe('true');
    expect(panel()).not.toBeNull();
    const second = options()[1];
    second.click();
    fixture.detectChanges();

    expect(host.form.value.role).toBe('tenant_admin');
    expect(trigger.textContent?.trim()).toBe('Workspace admin');
    expect(panel()).toBeNull();
    expect(document.activeElement).toBe(trigger);
  });

  it('keeps the form’s required law and says “Select…” when the value is empty', () => {
    const { fixture, host } = setup();
    const trigger = triggerOf(fixture);

    host.form.controls.role.setValue('');
    fixture.detectChanges();
    expect(host.form.controls.role.invalid).toBe(true);
    expect(trigger.textContent?.trim()).toBe('Select…');

    trigger.click();
    fixture.detectChanges();
    options()[0].click();
    fixture.detectChanges();

    expect(host.form.controls.role.valid).toBe(true);
    expect(trigger.textContent?.trim()).toBe('Viewer');
  });

  it('opens, moves and chooses from the keyboard alone, with aria-activedescendant speaking', () => {
    const { fixture, host } = setup();
    const trigger = triggerOf(fixture);
    trigger.focus();

    key(trigger, 'ArrowDown');
    fixture.detectChanges();

    expect(panel()).not.toBeNull();
    const first = options()[0];
    expect(trigger.getAttribute('aria-activedescendant')).toBe(first.id);
    expect(first.getAttribute('aria-selected')).toBe('true');

    key(trigger, 'ArrowDown');
    fixture.detectChanges();
    const second = options()[1];
    expect(trigger.getAttribute('aria-activedescendant')).toBe(second.id);
    expect(second.classList.contains('is-active')).toBe(true);

    key(trigger, 'Enter');
    fixture.detectChanges();

    expect(host.form.value.role).toBe('tenant_admin');
    expect(panel()).toBeNull();
    expect(document.activeElement).toBe(trigger);
  });

  it('skips disabled options in the keyboard walk', () => {
    const { fixture } = setup();
    const trigger = triggerOf(fixture);

    key(trigger, 'End');
    fixture.detectChanges();
    // the last option is disabled, so End lands on the last enabled one
    expect(trigger.getAttribute('aria-activedescendant')).toBe(options()[1].id);
  });

  it('leaves the value alone on Escape and does not close the dialog under it', () => {
    const { fixture, host } = setup();
    const trigger = triggerOf(fixture);
    trigger.focus();

    key(trigger, 'ArrowDown');
    fixture.detectChanges();
    key(trigger, 'ArrowDown');
    fixture.detectChanges();

    let escaped = false;
    trigger.parentElement?.addEventListener('keydown', (event) => {
      if ((event as KeyboardEvent).key === 'Escape') escaped = true;
    });

    key(trigger, 'Escape');
    fixture.detectChanges();

    expect(host.form.value.role).toBe('viewer');
    expect(panel()).toBeNull();
    expect(escaped).toBe(false);
  });

  it('finds an option by typing its first letters', () => {
    const { fixture } = setup();
    const trigger = triggerOf(fixture);
    trigger.focus();

    key(trigger, 'w');
    fixture.detectChanges();
    key(trigger, 'o');
    fixture.detectChanges();

    expect(trigger.getAttribute('aria-activedescendant')).toBe(options()[1].id);
  });

  it('serves a plain [value] binding as a controlled field', () => {
    const { fixture, host } = setup();
    const trigger = triggerOf(fixture, 'app-select.plain');
    expect(trigger.textContent?.trim()).toBe('Workspace admin');

    trigger.click();
    fixture.detectChanges();
    options()[0].click();
    fixture.detectChanges();

    expect(host.plain()).toBe('viewer');
    expect(trigger.textContent?.trim()).toBe('Viewer');
  });

  it('keeps a saving control focusable: busy says so but refuses to open', () => {
    const { fixture, host } = setup();
    host.busy.set(true);
    fixture.detectChanges();

    const trigger = triggerOf(fixture, 'app-select.bn');
    expect(trigger.disabled).toBe(false);
    expect(trigger.getAttribute('aria-disabled')).toBe('true');

    trigger.focus();
    trigger.click();
    fixture.detectChanges();

    expect(panel()).toBeNull();
    expect(document.activeElement).toBe(trigger);
  });

  it('never opens when disabled — by input or by the form control', () => {
    const { fixture, host } = setup();
    const off = triggerOf(fixture, 'app-select.off');
    expect(off.disabled).toBe(true);
    off.click();
    expect(panel()).toBeNull();

    host.form.controls.role.disable();
    fixture.detectChanges();
    const trigger = triggerOf(fixture, 'form app-select');
    expect(trigger.disabled).toBe(true);
  });

  it('re-renders its labels when the language changes', () => {
    const { fixture } = setup();
    const trigger = triggerOf(fixture);
    expect(trigger.textContent?.trim()).toBe('Viewer');

    TestBed.inject(TranslocoService).setActiveLang('ar');
    fixture.detectChanges();

    expect(trigger.textContent?.trim()).toBe('مشاهد');
  });

  it('follows the page direction at open time, not the direction it booted in', () => {
    const { fixture } = setup();
    const trigger = triggerOf(fixture);
    document.documentElement.setAttribute('dir', 'rtl');

    try {
      trigger.click();
      fixture.detectChanges();

      const pane = document.querySelector('.cdk-overlay-pane');
      // the overlay host carries the direction used for start/end alignment
      expect(pane?.parentElement?.getAttribute('dir')).toBe('rtl');
    } finally {
      document.documentElement.setAttribute('dir', 'ltr');
    }
  });

  it('tells a screen reader when the form field is required', () => {
    const { fixture } = setup();
    const trigger = triggerOf(fixture);
    expect(trigger.getAttribute('aria-required')).toBe('true');
  });

  it('closes on a pointer press outside without stealing focus back', () => {
    const { fixture } = setup();
    const trigger = triggerOf(fixture);
    trigger.focus();
    trigger.click();
    fixture.detectChanges();
    expect(panel()).not.toBeNull();

    document.body.dispatchEvent(new MouseEvent('mousedown', { bubbles: true }));
    document.body.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    fixture.detectChanges();

    expect(panel()).toBeNull();
  });
});
