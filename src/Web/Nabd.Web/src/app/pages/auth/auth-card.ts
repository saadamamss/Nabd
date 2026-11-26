import { Component, inject } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { LocaleService } from '../../core/i18n/locale.service';

/**
 * The anonymous screens' one shell (spec 07): brand, language toggle, a card.
 * The login page keeps its own hero layout on purpose — merging the two is a
 * design decision of its own, not a side effect of this spec.
 */
@Component({
  selector: 'app-auth-card',
  imports: [TranslocoPipe],
  templateUrl: './auth-card.html',
  styleUrl: './auth-card.scss',
})
export class AuthCard {
  readonly locale = inject(LocaleService);
}
