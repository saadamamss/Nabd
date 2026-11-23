import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { provideTestTransloco } from '../../testing/test-transloco';
import { AuthService } from '../../core/auth/auth.service';
import { DevicesPage } from './devices';

const SESSION = {
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

const DEVICE = {
  deviceId: 'd1',
  tenantId: 't1',
  assetId: 'a1',
  templateId: 'tpl-1',
  name: 'Probe 1',
  isActive: true,
  tokenRevokedAtUtc: null,
};

const TOKEN = 'nabd_tok_shown_once_123';

function setup(session = SESSION): {
  fixture: ComponentFixture<DevicesPage>;
  http: HttpTestingController;
  page: DevicesPage;
} {
  TestBed.configureTestingModule({
    imports: [DevicesPage],
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      ...provideTestTransloco(),
      {
        provide: ActivatedRoute,
        useValue: { snapshot: { paramMap: convertToParamMap({ assetId: 'a1' }) } },
      },
    ],
  });

  TestBed.inject(AuthService).apply(session);
  const http = TestBed.inject(HttpTestingController);
  const fixture = TestBed.createComponent(DevicesPage);

  return { fixture, http, page: fixture.componentInstance };
}

describe('DevicesPage', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('mints, shows the token exactly once, and never returns it on a refetch', async () => {
    const { fixture, http, page } = setup();
    fixture.detectChanges();
    http.expectOne('/api/v1/assets/a1/devices').flush([]);
    await fixture.whenStable();

    page.toggleCreate();
    page.form.setValue({ name: 'Probe 1' });
    page.mint();

    const mint = http.expectOne('/api/v1/assets/a1/devices');
    expect(mint.request.body).toEqual({ name: 'Probe 1' });
    mint.flush({ device: DEVICE, token: TOKEN });

    http.expectOne('/api/v1/assets/a1/devices').flush([DEVICE]);
    await fixture.whenStable();
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain(TOKEN);

    page.minted.set(null);
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).not.toContain(TOKEN);
  });

  it('reissues through the device door and reveals the new token', async () => {
    const { fixture, http, page } = setup();
    fixture.detectChanges();
    http.expectOne('/api/v1/assets/a1/devices').flush([DEVICE]);

    page.ask('reissue', DEVICE);
    page.run({ kind: 'reissue', device: DEVICE });

    const reissue = http.expectOne('/api/v1/devices/d1/token/reissue');
    expect(reissue.request.method).toBe('POST');
    reissue.flush({ device: DEVICE, token: 'new-token' });

    http.expectOne('/api/v1/assets/a1/devices').flush([DEVICE]);
    await fixture.whenStable();
    expect(page.minted()?.token).toBe('new-token');
  });

  it('revokes only after confirmation and refetches the list', async () => {
    const { fixture, http, page } = setup();
    fixture.detectChanges();
    http.expectOne('/api/v1/assets/a1/devices').flush([DEVICE]);

    page.ask('revoke', DEVICE);
    page.run({ kind: 'revoke', device: DEVICE });

    http.expectOne('/api/v1/devices/d1').flush(null, { status: 204, statusText: 'No Content' });
    http
      .expectOne('/api/v1/assets/a1/devices')
      .flush([{ ...DEVICE, isActive: false, tokenRevokedAtUtc: '2026-11-23T00:00:00Z' }]);
    await fixture.whenStable();

    expect(page.confirm()).toBeNull();
    expect(page.rows()[0].isActive).toBe(false);
  });

  it('offers no reissue or revoke on a revoked device (the door never re-enables it)', async () => {
    const { fixture, http } = setup();
    fixture.detectChanges();
    http
      .expectOne('/api/v1/assets/a1/devices')
      .flush([{ ...DEVICE, isActive: false, tokenRevokedAtUtc: '2026-11-23T00:00:00Z' }]);
    await fixture.whenStable();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.textContent).toContain('Revoked');

    const labels = [...host.querySelectorAll('button')].map((button) => button.textContent?.trim());
    expect(labels).not.toContain('Reissue token');
    expect(labels).not.toContain('Revoke');
  });

  it('keeps a viewer read-only: no mint, no reissue, no revoke', async () => {
    const viewer = { ...SESSION, user: { ...SESSION.user, role: 'viewer' } };
    const { fixture, http } = setup(viewer);
    fixture.detectChanges();
    http.expectOne('/api/v1/assets/a1/devices').flush([DEVICE]);
    await fixture.whenStable();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const labels = [...host.querySelectorAll('button')].map((button) => button.textContent?.trim());
    expect(labels).not.toContain('Add device');
    expect(labels).not.toContain('Reissue token');
    expect(labels).not.toContain('Revoke');
    expect(host.textContent).toContain('Probe 1');
  });
});
