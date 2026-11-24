import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideTestTransloco } from '../../testing/test-transloco';
import { AuthService } from '../../core/auth/auth.service';
import { NoticeService } from '../../core/ui/notice.service';
import { UsersPage } from './users';

const ADMIN_SESSION = {
  user: {
    userId: 'u1',
    tenantId: 't1',
    email: 'admin@nabd.test',
    role: 'tenant_admin',
    locale: 'en',
    displayName: 'Ada Admin',
  },
  accessToken: 'access-1',
};

const VIEWER_SESSION = {
  user: { ...ADMIN_SESSION.user, userId: 'u2', email: 'viewer@nabd.test', role: 'viewer', displayName: null },
  accessToken: 'access-2',
};

const ROWS = [
  {
    userId: 'u1',
    tenantId: 't1',
    email: 'admin@nabd.test',
    role: 'tenant_admin',
    locale: 'en',
    displayName: 'Ada Admin',
  },
  {
    userId: 'u2',
    tenantId: 't1',
    email: 'viewer@nabd.test',
    role: 'viewer',
    locale: 'ar',
    displayName: null,
  },
];

function setup(session: typeof ADMIN_SESSION | typeof VIEWER_SESSION): {
  fixture: ComponentFixture<UsersPage>;
  http: HttpTestingController;
  page: UsersPage;
} {
  TestBed.configureTestingModule({
    imports: [UsersPage],
    providers: [provideHttpClient(), provideHttpClientTesting(), ...provideTestTransloco()],
  });

  const auth = TestBed.inject(AuthService);
  auth.apply(session);

  const http = TestBed.inject(HttpTestingController);
  const fixture = TestBed.createComponent(UsersPage);
  const page = fixture.componentInstance;

  return { fixture, http, page };
}

describe('UsersPage', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('renders each row\'s real role and locale in its selects (never the first option)', async () => {
    const { fixture, http } = setup(ADMIN_SESSION);
    fixture.detectChanges();

    http.expectOne('/api/v1/tenants/t1/users').flush(ROWS);
    await fixture.whenStable();
    fixture.detectChanges();

    const selects = [
      ...(fixture.nativeElement as HTMLElement).querySelectorAll(
        'tbody app-select .ui-select-trigger',
      ),
    ];
    // row 1: role select + locale select, row 2: role select + locale select
    expect(selects.map((select) => select.textContent?.trim())).toEqual([
      'Workspace admin',
      'English',
      'Viewer',
      'العربية',
    ]);
    expect(selects[0].getAttribute('role')).toBe('combobox');
  });

  it('shows the display name and falls back to the email, never a blank cell', async () => {
    const { fixture, http, page } = setup(ADMIN_SESSION);
    fixture.detectChanges();

    http.expectOne('/api/v1/tenants/t1/users').flush(ROWS);
    await fixture.whenStable();
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Ada Admin');
    expect(text).toContain('viewer@nabd.test');
    expect(page.rows().length).toBe(2);
  });

  it('hides write affordances from a viewer but lets them read the list', async () => {
    const { fixture, http } = setup(VIEWER_SESSION);
    fixture.detectChanges();

    http.expectOne('/api/v1/tenants/t1/users').flush(ROWS);
    await fixture.whenStable();
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).not.toContain('Add person');
    expect(text).not.toContain('Delete');
    expect(text).toContain('Ada Admin');
  });

  it('creates a person with the door’s body and refetches the list', async () => {
    const { fixture, http, page } = setup(ADMIN_SESSION);
    fixture.detectChanges();
    http.expectOne('/api/v1/tenants/t1/users').flush(ROWS);

    page.openCreate();
    page.form.setValue({
      email: 'new@nabd.test',
      displayName: 'New Person',
      role: 'viewer',
      locale: 'ar',
      password: 'CorrectHorse1',
    });
    page.create();

    const create = http.expectOne('/api/v1/tenants/t1/users');
    expect(create.request.method).toBe('POST');
    expect(create.request.body).toEqual({
      email: 'new@nabd.test',
      displayName: 'New Person',
      locale: 'ar',
      password: 'CorrectHorse1',
      roleCode: 'viewer',
    });
    create.flush({ ...ROWS[1], userId: 'u3', email: 'new@nabd.test', displayName: 'New Person' });

    // No hand-patched array: the page asks the server again.
    http.expectOne('/api/v1/tenants/t1/users').flush([...ROWS]);
    await fixture.whenStable();
    expect(page.error()).toBeNull();
  });

  it('surfaces the last-admin refusal and keeps the server’s list', async () => {
    const { fixture, http, page } = setup(ADMIN_SESSION);
    fixture.detectChanges();
    http.expectOne('/api/v1/tenants/t1/users').flush(ROWS);

    page.remove(ROWS[0]);
    http
      .expectOne('/api/v1/tenants/t1/users/u1')
      .flush({ code: 'identity:last_admin' }, { status: 409, statusText: 'Conflict' });

    expect(page.error()?.key).toBe('errors.lastAdmin');
    http.expectOne('/api/v1/tenants/t1/users').flush(ROWS);
    await fixture.whenStable();
    expect(page.rows().length).toBe(2);
  });

  it('surfaces the self-delete refusal from the door', async () => {
    const { fixture, http, page } = setup(ADMIN_SESSION);
    fixture.detectChanges();
    http.expectOne('/api/v1/tenants/t1/users').flush(ROWS);

    page.remove(ROWS[0]);
    http
      .expectOne('/api/v1/tenants/t1/users/u1')
      .flush({ code: 'identity:self_delete' }, { status: 409, statusText: 'Conflict' });

    expect(page.error()?.key).toBe('errors.selfDelete');
    http.expectOne('/api/v1/tenants/t1/users').flush(ROWS);
  });

  it('renders the localized 403 even when a hidden action is forced', async () => {
    const { fixture, http, page } = setup(VIEWER_SESSION);
    fixture.detectChanges();
    http.expectOne('/api/v1/tenants/t1/users').flush(ROWS);

    // The button is hidden from a viewer; the method is still callable.
    page.remove(ROWS[1]);
    http
      .expectOne('/api/v1/tenants/t1/users/u2')
      .flush({ code: 'identity:forbidden' }, { status: 403, statusText: 'Forbidden' });

    expect(page.error()?.key).toBe('errors.forbidden');
    http.expectOne('/api/v1/tenants/t1/users').flush(ROWS);
  });

  it('adds a person in a dialog: the form is absent until asked, focused on open, and Cancel closes it with no request', async () => {
    const { fixture, http } = setup(ADMIN_SESSION);
    fixture.detectChanges();
    http.expectOne('/api/v1/tenants/t1/users').flush(ROWS);
    await fixture.whenStable();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    // the form is not a permanent panel above the table
    expect(host.querySelector('[role="dialog"]')).toBeNull();

    const add = [...host.querySelectorAll('button')].find(
      (button) => button.textContent?.trim() === 'Add person',
    ) as HTMLButtonElement;
    add.click();
    fixture.detectChanges();

    const dialog = host.querySelector('[role="dialog"]') as HTMLElement;
    expect(dialog).not.toBeNull();
    expect(dialog.textContent).toContain('New person');
    // the control values are rendered, not just held: locale defaults to en while
    // Arabic is the first option
    const selects = [...dialog.querySelectorAll('app-select .ui-select-trigger')];
    expect(selects.map((trigger) => trigger.textContent?.trim())).toEqual([
      'Viewer',
      'English',
    ]);

    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(document.activeElement?.getAttribute('formcontrolname')).toBe('email');

    const cancel = [...dialog.querySelectorAll('button')].find(
      (button) => button.textContent?.trim() === 'Cancel',
    ) as HTMLButtonElement;
    cancel.click();
    fixture.detectChanges();

    expect(host.querySelector('[role="dialog"]')).toBeNull();
    // afterEach's http.verify() proves nothing left the page
  });

  it('closes the dialog on Escape — the keyboard must be able to back out', async () => {
    const { fixture, http } = setup(ADMIN_SESSION);
    fixture.detectChanges();
    http.expectOne('/api/v1/tenants/t1/users').flush(ROWS);

    const host = fixture.nativeElement as HTMLElement;
    ([...host.querySelectorAll('button')] as HTMLButtonElement[])
      .find((button) => button.textContent?.trim() === 'Add person')!
      .click();
    fixture.detectChanges();

    (host.querySelector('[role="dialog"]') as HTMLElement).dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }),
    );
    fixture.detectChanges();

    expect(host.querySelector('[role="dialog"]')).toBeNull();
  });

  it('shows a refusal inside the open dialog instead of behind the overlay', async () => {
    const { fixture, http, page } = setup(ADMIN_SESSION);
    fixture.detectChanges();
    http.expectOne('/api/v1/tenants/t1/users').flush(ROWS);

    page.openCreate();
    page.form.setValue({
      email: 'taken@nabd.test',
      displayName: 'Taken',
      role: 'viewer',
      locale: 'en',
      password: 'CorrectHorse1',
    });
    page.create();
    http
      .expectOne('/api/v1/tenants/t1/users')
      .flush({ code: 'identity:email_taken' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    const dialog = (fixture.nativeElement as HTMLElement).querySelector(
      '[role="dialog"]',
    ) as HTMLElement;
    expect(dialog.textContent).toContain('That email is already used');
    expect(page.creating()).toBe(true);
  });

  it('asks before deleting: the dialog names the person and no request fires until confirm', async () => {
    const { fixture, http, page } = setup(ADMIN_SESSION);
    fixture.detectChanges();
    http.expectOne('/api/v1/tenants/t1/users').flush(ROWS);
    await fixture.whenStable();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const deleteButtons = [...host.querySelectorAll('button')].filter(
      (button) => button.textContent?.trim() === 'Delete',
    );
    expect(deleteButtons.length).toBe(2);
    const deleteButton = deleteButtons[1] as HTMLButtonElement;
    deleteButton.click();
    fixture.detectChanges();

    const dialog = host.querySelector('[role="dialog"]');
    expect(dialog).not.toBeNull();
    expect(dialog?.textContent).toContain('viewer@nabd.test');
    // nothing left the page yet: http.verify() in afterEach would catch a stray DELETE
    expect(page.removeTarget()?.userId).toBe('u2');

    const confirm = [...(dialog as HTMLElement).querySelectorAll('button')].find(
      (button) => button.textContent?.trim() === 'Remove',
    ) as HTMLButtonElement;
    confirm.click();
    const request = http.expectOne('/api/v1/tenants/t1/users/u2');
    expect(request.request.method).toBe('DELETE');
    request.flush(null);
    http.expectOne('/api/v1/tenants/t1/users').flush([ROWS[0]]);
  });

  it('says each answer out loud: a success toast after a mutation, an error toast on a refusal', async () => {
    const { fixture, http, page } = setup(ADMIN_SESSION);
    const notices = TestBed.inject(NoticeService);
    fixture.detectChanges();
    http.expectOne('/api/v1/tenants/t1/users').flush(ROWS);

    page.changeRole(ROWS[1], 'tenant_admin');
    http.expectOne('/api/v1/tenants/t1/users/u2/roles').flush({ ...ROWS[1], role: 'tenant_admin' });

    expect(notices.notices().map((notice) => [notice.key, notice.kind])).toEqual([
      ['toast.roleChanged', 'success'],
    ]);

    page.remove(ROWS[0]);
    http
      .expectOne('/api/v1/tenants/t1/users/u1')
      .flush({ code: 'identity:last_admin' }, { status: 409, statusText: 'Conflict' });
    http.expectOne('/api/v1/tenants/t1/users').flush(ROWS);

    // newest first: the refusal is the answer on top of the stack
    const last = notices.notices()[0];
    expect([last.key, last.kind]).toEqual(['errors.lastAdmin', 'error']);
  });

  it('changes a role from the drawn select and paints the door’s own row', async () => {
    const { fixture, http } = setup(ADMIN_SESSION);
    fixture.detectChanges();
    http.expectOne('/api/v1/tenants/t1/users').flush(ROWS);
    await fixture.whenStable();
    fixture.detectChanges();

    const trigger = [
      ...(fixture.nativeElement as HTMLElement).querySelectorAll(
        'tbody app-select .ui-select-trigger',
      ),
    ][2] as HTMLButtonElement;
    trigger.click();
    fixture.detectChanges();

    const option = [...document.querySelectorAll<HTMLElement>('.ui-select-option')].find(
      (item) => item.textContent?.trim() === 'Workspace admin',
    );
    option!.click();
    fixture.detectChanges();

    const patch = http.expectOne('/api/v1/tenants/t1/users/u2/roles');
    expect(patch.request.body).toEqual({ role: 'tenant_admin' });
    // saving is announced without taking the control (and focus) away
    expect(trigger.getAttribute('aria-disabled')).toBe('true');
    expect(document.activeElement).toBe(trigger);

    patch.flush({ ...ROWS[1], role: 'tenant_admin' });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(trigger.textContent?.trim()).toBe('Workspace admin');
    expect(trigger.getAttribute('aria-disabled')).toBeNull();
    expect(document.activeElement).toBe(trigger);
  });

  it('puts the old role back when the door refuses the change', async () => {
    const { fixture, http, page } = setup(ADMIN_SESSION);
    fixture.detectChanges();
    http.expectOne('/api/v1/tenants/t1/users').flush(ROWS);
    await fixture.whenStable();
    fixture.detectChanges();

    page.changeRole(ROWS[1], 'tenant_admin');
    http
      .expectOne('/api/v1/tenants/t1/users/u2/roles')
      .flush({ code: 'identity:forbidden' }, { status: 403, statusText: 'Forbidden' });
    http.expectOne('/api/v1/tenants/t1/users').flush(ROWS);
    await fixture.whenStable();
    fixture.detectChanges();

    const triggers = [
      ...(fixture.nativeElement as HTMLElement).querySelectorAll(
        'tbody app-select .ui-select-trigger',
      ),
    ];
    expect(triggers[2].textContent?.trim()).toBe('Viewer');
  });

  it('paints the PATCH answer in place: no list reload, so the table never blinks', async () => {
    const { fixture, http, page } = setup(ADMIN_SESSION);
    fixture.detectChanges();
    http.expectOne('/api/v1/tenants/t1/users').flush(ROWS);

    page.changeRole(ROWS[1], 'tenant_admin');
    const patch = http.expectOne('/api/v1/tenants/t1/users/u2/roles');
    expect(patch.request.body).toEqual({ role: 'tenant_admin' });
    // the door's own row is the truth, even when it differs from what was sent
    patch.flush({ ...ROWS[1], role: 'tenant_admin', displayName: 'Renamed by the door' });
    fixture.detectChanges();

    // http.verify() in afterEach proves no second GET was fired
    expect(page.rows()[1].role).toBe('tenant_admin');
    expect(page.rows()[1].displayName).toBe('Renamed by the door');
    expect(page.loading()).toBe(false);
    expect(page.pendingId()).toBeNull();
  });

  it('keeps the table on screen during a quiet refresh after a removal', async () => {
    const { fixture, http, page } = setup(ADMIN_SESSION);
    fixture.detectChanges();
    http.expectOne('/api/v1/tenants/t1/users').flush(ROWS);
    await fixture.whenStable();
    fixture.detectChanges();

    page.remove(ROWS[1]);
    http.expectOne('/api/v1/tenants/t1/users/u2').flush(null);
    const refresh = http.expectOne('/api/v1/tenants/t1/users');

    // the GET is in flight, yet the rows stay and no loading line replaces them
    fixture.detectChanges();
    expect(page.loading()).toBe(false);
    expect(page.rows().length).toBe(2);
    expect((fixture.nativeElement as HTMLElement).querySelector('table')).not.toBeNull();

    refresh.flush([ROWS[0]]);
    await fixture.whenStable();
    expect(page.rows().length).toBe(1);
  });
});
