/**
 * Spec 01, "Direction discipline": physical direction properties are banned in
 * `src/Web`; the same stylesheet must serve LTR and RTL. Overrides for
 * third-party internals belong in a documented exceptions file, not here.
 */
export default {
  extends: ['stylelint-config-standard-scss'],
  rules: {
    'property-disallowed-list': [
      'margin-left',
      'margin-right',
      'padding-left',
      'padding-right',
      'border-left',
      'border-right',
      'border-left-color',
      'border-right-color',
      'border-left-width',
      'border-right-width',
      'border-top-left-radius',
      'border-top-right-radius',
      'border-bottom-left-radius',
      'border-bottom-right-radius',
      'left',
      'right',
      'float',
    ],
    'declaration-property-value-disallowed-list': {
      '/^text-align$/': ['left', 'right'],
      float: ['left', 'right'],
    },
  },
};
