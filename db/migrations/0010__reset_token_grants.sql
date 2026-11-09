-- 0010__reset_token_grants.sql — repair for the half-applied 0009.
--
-- 0009 first ran into dev databases BEFORE its GRANT section existed
-- (the 42501 "permission denied for table password_reset_tokens" from the
-- live reset-request probe of 2026-11-10). The ledger records 0009 as done,
-- so the fixed file alone never reaches those databases. GRANT is idempotent,
-- so a forward-only repair is safe to re-run anywhere.
--
-- Same grants 0009 carries today; fresh databases need neither, old ones get both.
GRANT INSERT, UPDATE, SELECT ON identity.password_reset_tokens TO nabd_app;
GRANT USAGE ON SEQUENCE identity.password_reset_tokens_id_seq TO nabd_app;
