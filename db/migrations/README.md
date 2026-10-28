# db/migrations

Versioned SQL migrations. The schema's truth lives here — plan/10 §3 P0 ("migrations as code").

## The law

- **Naming:** `NNNN__<name>.sql`, zero-padded to 4 digits (`0001__identity.sql`).
  Zero-padding is load-bearing, not cosmetic: the shell sorts files lexically, and
  `0010__x.sql` must sort *after* `0009__y.sql`. The sort order defines the apply order.
- **Never edit an applied file.** Once `NNNN__name.sql` has run (recorded in
  `platform.migrations_applied`), fixes arrive as NEW files (expand/contract, plan/08 §3).
- **Self-sufficiency:** each file must succeed alone against a fresh database.
  It creates/ensures everything it uses: its own `SCHEMA IF NOT EXISTS`, its own
  extensions (`citext` in 0001, `timescaledb` in 0003), its own `IF NOT EXISTS` guards.
- **Files are reviewed as code** — applied output is scrolled and expected per spec
  (`phases/P0/02-schema-baseline.md`); wild-animal SQL is not a thing here.

## Applying

```bash
bash db/migrations/_apply.sh
```

- Applies every `NNNN__*.sql` not yet recorded in `platform.migrations_applied`, in
  lexical order (`0001 → 0002 → 0003 → …`), skipping the ones already applied.
- **Atomic per file:** the file's statements + its tracker-row INSERT run inside one
  transaction (`psql -1`). A failed migration is never recorded as applied.
- The script bootstraps `platform.migrations_applied` itself (idempotent) — so it works
  on a brand-new database as well as an existing one.

## Rebuild-from-zero (the habit that replaces hope)

Dev database is disposable:

```bash
docker compose --env-file .env -f docker-compose.dev.yml down -v   # destroys volume
docker compose --env-file .env -f docker-compose.dev.yml up -d
bash db/migrations/_apply.sh
```

If this doesn't reproduce a green, fully-populated schema from nothing, the files are
broken — before any other theory is entertained.
