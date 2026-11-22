import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { NoticesComponent } from './layout/notices/notices';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, NoticesComponent],
  template: `
    <router-outlet />
    <app-notices />
  `,
})
export class App {}
