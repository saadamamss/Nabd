import { TestBed } from '@angular/core/testing';
import { NoticeService } from './notice.service';

describe('NoticeService', () => {
  let notices: NoticeService;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    notices = TestBed.inject(NoticeService);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('keeps a success on screen, then removes it on its own', () => {
    vi.useFakeTimers();
    notices.show('toast.roleChanged', 'success');

    expect(notices.notices().map((notice) => notice.kind)).toEqual(['success']);
    expect(notices.notices()[0].key).toBe('toast.roleChanged');

    vi.advanceTimersByTime(3999);
    expect(notices.notices().length).toBe(1);
    vi.advanceTimersByTime(1);
    expect(notices.notices()).toEqual([]);
  });

  it('never auto-dismisses an error or a session notice — the user closes it', () => {
    vi.useFakeTimers();
    notices.show('errors.lastAdmin', 'error');
    notices.show('auth.sessionExpired');

    vi.advanceTimersByTime(60_000);
    expect(notices.notices().length).toBe(2);

    const session = notices.notices().find((notice) => notice.key === 'auth.sessionExpired')!;
    notices.dismiss(session.id);
    expect(notices.notices().map((notice) => notice.key)).toEqual(['errors.lastAdmin']);
  });

  it('stacks newest first, so the last answer is the one you read', () => {
    notices.show('toast.created', 'success');
    notices.show('toast.roleChanged', 'success');

    expect(notices.notices().map((notice) => notice.key)).toEqual([
      'toast.roleChanged',
      'toast.created',
    ]);
  });

  it('holds the countdown still while the pointer is on it, then resumes with what was left', () => {
    vi.useFakeTimers();
    notices.show('toast.guardSaved', 'success');
    const id = notices.notices()[0].id;

    vi.advanceTimersByTime(1500);
    notices.hold(id);
    vi.advanceTimersByTime(30_000);
    expect(notices.notices().length).toBe(1);

    notices.release(id);
    vi.advanceTimersByTime(2499);
    expect(notices.notices().length).toBe(1);
    vi.advanceTimersByTime(1);
    expect(notices.notices()).toEqual([]);
  });

  it('caps the stack at five and drops the oldest with its clock', () => {
    vi.useFakeTimers();
    for (let index = 1; index <= 6; index++) notices.show(`toast.n${index}`, 'success');

    expect(notices.notices().length).toBe(5);
    expect(notices.notices()[0].key).toBe('toast.n6');
    expect(notices.notices().map((notice) => notice.key)).not.toContain('toast.n1');

    vi.advanceTimersByTime(4000);
    expect(notices.notices()).toEqual([]);
  });
});
