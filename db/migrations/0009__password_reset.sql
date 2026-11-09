-- 0009__password_reset.sql — P1 spec 05 (plan/07 §3 row 4): password reset flow.
-- Same audited-exception pattern as 0007/0008 (plan/04 §10). The reset lane is
-- PRE-auth: the resolution key is the EMAIL (request) and the TOKEN HASH
-- (confirm) — no session, no tenant known. We reuse find_login_user for the
-- email door and open exactly ONE new door for the hash door. Never widen.

BEGIN;

-- (a) the token table — refresh_tokens' shape family (0004):
--     token_hashes ONLY; the single-use law is used_at; the 15-min expiry is data.
CREATE TABLE IF NOT EXISTS identity.password_reset_tokens (
    id         bigserial   PRIMARY KEY,
    user_id    uuid        NOT NULL REFERENCES identity.users(id) ON DELETE CASCADE,
    token_hash text        NOT NULL,
    expires_at timestamptz NOT NULL,
    used_at    timestamptz,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_reset_token_hash ON identity.password_reset_tokens (token_hash);

-- (b) the tenant wall — the SAME EXISTS-shape refresh_tokens got in 0006:
--     a reset row belongs to whoever the USER belongs to.
ALTER TABLE identity.password_reset_tokens ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_password_reset_tokens ON identity.password_reset_tokens
    USING (EXISTS (
        SELECT 1 FROM identity.users u
        WHERE u.id = user_id
          AND u.tenant_id = current_setting('app.current_tenant', true)::uuid
    ));

-- (c) audit amendment (not a widening): the email door now serves reset-request
--     too — one door into users-by-email stays THE door.
COMMENT ON FUNCTION identity.find_login_user(text) IS
    'P1 spec 03+05 audited exception: the sole pre-auth path into identity.users (plan/04 §10). Login/refresh + reset-request flows only.';

-- (d) pre-auth door #2: hash → the bits the confirm lane needs. Hash is UNIQUE
--     (unique index above) so LIMIT 1 is guaranteed, same reasoning as 0008.
CREATE OR REPLACE FUNCTION identity.find_reset_token(p_hash text)
RETURNS TABLE (
    user_id    uuid,
    tenant_id  uuid,
    expires_at timestamptz,
    used_at    timestamptz
)
LANGUAGE sql STABLE
SECURITY DEFINER
SET search_path = identity, pg_temp
AS $$     SELECT r.user_id, u.tenant_id, r.expires_at, r.used_at
    FROM identity.password_reset_tokens r
    JOIN identity.users u ON u.id = r.user_id
    WHERE r.token_hash = p_hash
    LIMIT 1;
 $$;

REVOKE ALL ON FUNCTION identity.find_reset_token(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION identity.find_reset_token(text) TO nabd_app;

-- (e) the APP must work its own table: 0005 granted the tables that existed THEN;
--     a new table needs its own grants (caught live: 42501 in the request lane).
GRANT INSERT, UPDATE, SELECT ON identity.password_reset_tokens TO nabd_app;
GRANT USAGE ON SEQUENCE identity.password_reset_tokens_id_seq TO nabd_app;

COMMIT;