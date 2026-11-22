import stylelint from 'stylelint';

// The project's real config: a seeded physical property must fail `npm run lint`.
// @ts-ignore - plain .mjs config without typings.
const { default: config } = await import('../../../stylelint.config.mjs');

describe('logical-properties lint law', () => {
  it('a seeded margin-left is rejected', async () => {
    const result = await stylelint.lint({
      code: '.seeded { margin-left: 1rem; }',
      config,
    });

    const rules = result.results[0].warnings.map((warning) => warning.rule);
    expect(rules).toContain('property-disallowed-list');
  });

  it('a seeded text-align: right is rejected', async () => {
    const result = await stylelint.lint({
      code: '.seeded { text-align: right; }',
      config,
    });

    const rules = result.results[0].warnings.map((warning) => warning.rule);
    expect(rules).toContain('declaration-property-value-disallowed-list');
  });

  it('the logical equivalent passes', async () => {
    const result = await stylelint.lint({
      code: '.clean { margin-inline-start: 1rem; text-align: end; }',
      config,
    });

    expect(result.results[0].warnings).toEqual([]);
  });
});
