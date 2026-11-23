import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideTestTransloco } from '../../testing/test-transloco';
import { AuthService } from '../../core/auth/auth.service';
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
      ...(fixture.nativeElement as HTMLElement).querySelectorAll('select'),
    ] as HTMLSelectElement[];
    // row 1: role select + locale select, row 2: role select + locale select
    expect(selects.map((select) => select.value)).toEqual([
      'tenant_admin',
      'en',
      'viewer',
      'ar',
    ]);
    expect(selects[0].selectedOptions[0]?.textContent?.trim()).toBe('Workspace admin');
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

    page.toggleCreate();
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

  it('refetches after a role change instead of trusting the local row', async () => {
    const { fixture, http, page } = setup(ADMIN_SESSION);
    fixture.detectChanges();
    http.expectOne('/api/v1/tenants/t1/users').flush(ROWS);

    page.changeRole(ROWS[1], 'tenant_admin');
    const patch = http.expectOne('/api/v1/tenants/t1/users/u2/roles');
    expect(patch.request.body).toEqual({ role: 'tenant_admin' });
    patch.flush({ ...ROWS[1], role: 'tenant_admin' });

    http.expectOne('/api/v1/tenants/t1/users').flush([ROWS[0], { ...ROWS[1], role: 'tenant_admin' }]);
    await fixture.whenStable();
    expect(page.rows()[1].role).toBe('tenant_admin');
  });
});
