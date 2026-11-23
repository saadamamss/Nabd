import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';
import { provideTestTransloco } from './testing/test-transloco';

describe('App', () => {
  it('creates the root component with its outlet and notice host', async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter([]), ...provideTestTransloco()],
    }).compileComponents();

    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();

    expect(fixture.componentInstance).toBeTruthy();
    expect((fixture.nativeElement as HTMLElement).querySelector('app-notices')).toBeTruthy();
  });
});
