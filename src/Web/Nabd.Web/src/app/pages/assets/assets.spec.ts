import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideTestTransloco } from '../../testing/test-transloco';
import { AuthService } from '../../core/auth/auth.service';
import { AssetsPage } from './assets';

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

const TEMPLATE = {
  templateId: 'tpl-1',
  key: 'fridge',
  nameAr: 'ثلاجة',
  nameEn: 'Fridge',
  channels: [
    { key: 'temp', nameAr: 'الحرارة', nameEn: 'Temperature', unit: '°C', dataType: 'number', lowerLimit: 2, upperLimit: 8 },
  ],
};

const ASSET = {
  assetId: 'a1',
  tenantId: 't1',
  templateId: 'tpl-1',
  name: 'Cold room',
  location: 'Aisle 3',
  notes: null,
  isActive: true,
};

function setup(session = ADMIN_SESSION): {
  fixture: ComponentFixture<AssetsPage>;
  http: HttpTestingController;
  page: AssetsPage;
} {
  TestBed.configureTestingModule({
    imports: [AssetsPage],
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      ...provideTestTransloco(),
    ],
  });

  TestBed.inject(AuthService).apply(session);
  const http = TestBed.inject(HttpTestingController);
  const fixture = TestBed.createComponent(AssetsPage);

  return { fixture, http, page: fixture.componentInstance };
}

describe('AssetsPage', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('creates with the picked template and shows its channels', async () => {
    const { fixture, http, page } = setup();
    fixture.detectChanges();

    http.expectOne((request) => request.url === '/api/v1/assets').flush([]);
    http.expectOne('/api/v1/templates').flush([TEMPLATE]);
    await fixture.whenStable();

    page.openCreate();
    page.form.setValue({ templateId: 'tpl-1', name: 'Cold room', location: 'Aisle 3', notes: '' });
    page.templateChanged();
    fixture.detectChanges();

    expect(page.selectedTemplate()?.channels.length).toBe(1);

    page.create();
    const create = http.expectOne('/api/v1/assets');
    expect(create.request.method).toBe('POST');
    expect(create.request.body).toEqual({
      templateId: 'tpl-1',
      name: 'Cold room',
      location: 'Aisle 3',
      notes: null,
    });
    create.flush(ASSET);
    http.expectOne((request) => request.url === '/api/v1/assets').flush([ASSET]);
    await fixture.whenStable();
    expect(page.rows().length).toBe(1);
  });

  it('opens the create form in a dialog, not a panel above the table, and Cancel leaves without a request', async () => {
    const { fixture, http } = setup();
    fixture.detectChanges();
    http.expectOne((request) => request.url === '/api/v1/assets').flush([]);
    http.expectOne('/api/v1/templates').flush([TEMPLATE]);
    await fixture.whenStable();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[role="dialog"]')).toBeNull();

    ([...host.querySelectorAll('button')] as HTMLButtonElement[])
      .find((button) => button.textContent?.trim() === 'Add asset')!
      .click();
    fixture.detectChanges();

    const dialog = host.querySelector('[role="dialog"]') as HTMLElement;
    expect(dialog).not.toBeNull();
    expect(dialog.textContent).toContain('New asset');
    expect(dialog.querySelectorAll('form').length).toBe(1);

    ([...dialog.querySelectorAll('button')] as HTMLButtonElement[])
      .find((button) => button.textContent?.trim() === 'Cancel')!
      .click();
    fixture.detectChanges();

    expect(host.querySelector('[role="dialog"]')).toBeNull();
  });

  it('archives only after confirmation and refetches', async () => {
    const { fixture, http, page } = setup();
    fixture.detectChanges();
    http.expectOne((request) => request.url === '/api/v1/assets').flush([ASSET]);
    http.expectOne('/api/v1/templates').flush([TEMPLATE]);

    page.archiveTarget.set(ASSET);
    page.archive(ASSET);

    http.expectOne('/api/v1/assets/a1').flush(null, { status: 204, statusText: 'No Content' });
    http.expectOne((request) => request.url === '/api/v1/assets').flush([]);
    await fixture.whenStable();

    expect(page.archiveTarget()).toBeNull();
    expect(page.rows()).toEqual([]);
  });

  it('draws the checkbox but keeps a real one for keyboards and screen readers', async () => {
    const { fixture, http } = setup();
    fixture.detectChanges();
    http.expectOne((request) => request.url === '/api/v1/assets').flush([ASSET]);
    http.expectOne('/api/v1/templates').flush([TEMPLATE]);
    await fixture.whenStable();
    fixture.detectChanges();

    const checkbox = (fixture.nativeElement as HTMLElement).querySelector(
      'input[type="checkbox"]',
    ) as HTMLInputElement;
    expect(checkbox).not.toBeNull();
    expect(checkbox.disabled).toBe(false);
    expect(checkbox.nextElementSibling?.classList.contains('ui-check-box')).toBe(true);
    expect(checkbox.nextElementSibling?.getAttribute('aria-hidden')).toBe('true');
  });

  it('picks a template from the drawn list and shows its channels at once', async () => {
    const { fixture, http, page } = setup();
    fixture.detectChanges();
    http.expectOne((request) => request.url === '/api/v1/assets').flush([]);
    http.expectOne('/api/v1/templates').flush([TEMPLATE]);
    await fixture.whenStable();

    page.openCreate();
    fixture.detectChanges();

    const dialog = (fixture.nativeElement as HTMLElement).querySelector('[role="dialog"]') as HTMLElement;
    const trigger = dialog.querySelector('app-select .ui-select-trigger') as HTMLButtonElement;
    // the first field and the required one: the cursor starts on the picker
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(document.activeElement).toBe(trigger);
    expect(trigger.textContent?.trim()).toBe('Select…');

    trigger.click();
    fixture.detectChanges();
    const fridge = [...document.querySelectorAll<HTMLElement>('.ui-select-option')].find(
      (option) => option.textContent?.trim() === 'Fridge',
    );
    fridge!.click();
    fixture.detectChanges();

    expect(page.form.value.templateId).toBe('tpl-1');
    expect(page.selectedTemplate()?.channels.length).toBe(1);
    expect(trigger.textContent?.trim()).toBe('Fridge');
  });

  it('refreshes quietly after an archive — the table never swaps for the loading line', async () => {
    const { fixture, http, page } = setup();
    fixture.detectChanges();
    http.expectOne((request) => request.url === '/api/v1/assets').flush([ASSET]);
    http.expectOne('/api/v1/templates').flush([TEMPLATE]);
    await fixture.whenStable();
    fixture.detectChanges();

    page.archive(ASSET);
    http.expectOne('/api/v1/assets/a1').flush(null, { status: 204, statusText: 'No Content' });
    const refresh = http.expectOne((request) => request.url === '/api/v1/assets');

    fixture.detectChanges();
    expect(page.loading()).toBe(false);
    expect(page.rows().length).toBe(1);
    expect((fixture.nativeElement as HTMLElement).querySelector('table')).not.toBeNull();

    refresh.flush([]);
    await fixture.whenStable();
  });

  it('maps a refused create to the localized code and keeps the form open', async () => {
    const { fixture, http, page } = setup();
    fixture.detectChanges();
    http.expectOne((request) => request.url === '/api/v1/assets').flush([]);
    http.expectOne('/api/v1/templates').flush([TEMPLATE]);

    page.openCreate();
    page.form.setValue({ templateId: 'tpl-9', name: 'Cold room', location: '', notes: '' });
    page.create();

    http
      .expectOne('/api/v1/assets')
      .flush({ code: 'assets:unknown_template' }, { status: 422, statusText: 'Unprocessable' });

    expect(page.error()?.key).toBe('errors.unknownTemplate');
    expect(page.creating()).toBe(true);
  });

  it('maps a whitespace-only name refusal (the door trims) to its own line', async () => {
    const { fixture, http, page } = setup();
    fixture.detectChanges();
    http.expectOne((request) => request.url === '/api/v1/assets').flush([]);
    http.expectOne('/api/v1/templates').flush([TEMPLATE]);

    page.openCreate();
    page.form.setValue({ templateId: 'tpl-1', name: '   ', location: '', notes: '' });
    page.create();

    http
      .expectOne('/api/v1/assets')
      .flush({ code: 'assets:invalid_name' }, { status: 400, statusText: 'Bad Request' });

    expect(page.error()?.key).toBe('errors.invalidName');
  });

  it('asks the server for archived rows only when the toggle is on', async () => {
    const { fixture, http, page } = setup();
    fixture.detectChanges();
    http.expectOne((request) => request.url === '/api/v1/assets').flush([]);
    http.expectOne('/api/v1/templates').flush([TEMPLATE]);
    await fixture.whenStable();

    page.toggleArchived({ target: { checked: true } } as unknown as Event);

    const reload = http.expectOne((request) => request.url === '/api/v1/assets');
    expect(reload.request.params.get('includeArchived')).toBe('true');
    reload.flush([{ ...ASSET, isActive: false }]);
    await fixture.whenStable();
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Archived');
  });

  it('keeps a viewer read-only', async () => {
    const viewer = { ...ADMIN_SESSION, user: { ...ADMIN_SESSION.user, role: 'viewer' } };
    const { fixture, http } = setup(viewer);
    fixture.detectChanges();
    http.expectOne((request) => request.url === '/api/v1/assets').flush([ASSET]);
    http.expectOne('/api/v1/templates').flush([TEMPLATE]);
    await fixture.whenStable();
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).not.toContain('Add asset');
    expect(text).not.toContain('Archive');
    expect(text).toContain('Cold room');
    // The list resolves the template id to its name from /templates.
    expect(text).toContain('Fridge');
  });
});
