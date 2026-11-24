import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideTestTransloco } from '../../testing/test-transloco';
import { NoticeService } from '../../core/ui/notice.service';
import { NoticesComponent } from './notices';

function setup(): {
  fixture: ComponentFixture<NoticesComponent>;
  notices: NoticeService;
} {
  TestBed.configureTestingModule({
    imports: [NoticesComponent],
    providers: [...provideTestTransloco()],
  });

  const notices = TestBed.inject(NoticeService);
  const fixture = TestBed.createComponent(NoticesComponent);
  fixture.detectChanges();

  return { fixture, notices };
}

describe('NoticesComponent', () => {
  it('renders a success as a polite status with the tick and its countdown', async () => {
    const { fixture, notices } = setup();

    notices.show('toast.roleChanged', 'success');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const node = (fixture.nativeElement as HTMLElement).querySelector('.notice') as HTMLElement;
    expect(node.getAttribute('role')).toBe('status');
    expect(node.getAttribute('aria-live')).toBe('polite');
    expect(node.classList.contains('is-success')).toBe(true);
    expect(node.textContent).toContain('Role changed.');
    expect(node.querySelector('.notice-meter')).not.toBeNull();
  });

  it('renders a refusal as an assertive alert with no countdown, and dismiss removes it', async () => {
    const { fixture, notices } = setup();

    notices.show('errors.lastAdmin', 'error');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const node = (fixture.nativeElement as HTMLElement).querySelector('.notice') as HTMLElement;
    expect(node.getAttribute('role')).toBe('alert');
    expect(node.getAttribute('aria-live')).toBe('assertive');
    expect(node.classList.contains('is-error')).toBe(true);
    expect(node.querySelector('.notice-meter')).toBeNull();

    (node.querySelector('.notice-dismiss') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('.notice')).toBeNull();
    expect(notices.notices()).toEqual([]);
  });

  it('freezes the countdown while the pointer is on the card, and lets it go on leave', () => {
    const { fixture, notices } = setup();
    notices.show('toast.guardSaved', 'success');
    fixture.detectChanges();

    const node = (fixture.nativeElement as HTMLElement).querySelector('.notice') as HTMLElement;
    node.dispatchEvent(new MouseEvent('mouseenter'));
    fixture.detectChanges();
    expect(node.classList.contains('is-held')).toBe(true);

    node.dispatchEvent(new MouseEvent('mouseleave'));
    fixture.detectChanges();
    expect(node.classList.contains('is-held')).toBe(false);
  });
});
