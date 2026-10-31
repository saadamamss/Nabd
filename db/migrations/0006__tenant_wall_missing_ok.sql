-- 0006__tenant_wall_missing_ok.sql — P1 spec 02 step 0 (follow-up probe fix)
-- Law: plan/04 §10 (DB rejects unfiltered queries) — behaviour chosen: "fail SHUT",
--      not "fail LOUD". Replaces the four policies' current_setting(...) with the
--      missing_ok form so an app connection that forgot SET LOCAL sees NOTHING
--      (zero rows) instead of erroring; a wrong-tenant setting also sees nothing.
-- Rationale on record (phases/P1/NOTES 2026-10-30): the probe for the app role
-- surfaced current_setting's throw-when-unset; the wall is defense, not diagnostics.

BEGIN;

DROP POLICY IF EXISTS tenant_isolation_tenants        ON identity.tenants;
DROP POLICY IF EXISTS tenant_isolation_users          ON identity.users;
DROP POLICY IF EXISTS tenant_isolation_user_roles     ON identity.user_roles;
DROP POLICY IF EXISTS tenant_isolation_refresh_tokens ON identity.refresh_tokens;

CREATE POLICY tenant_isolation_tenants ON identity.tenants
    USING (id = current_setting('app.current_tenant', true)::uuid);
CREATE POLICY tenant_isolation_users ON identity.users
    USING (tenant_id = current_setting('app.current_tenant', true)::uuid);
CREATE POLICY tenant_isolation_user_roles ON identity.user_roles
    USING (tenant_id = current_setting('app.current_tenant', true)::uuid);
CREATE POLICY tenant_isolation_refresh_tokens ON identity.refresh_tokens
    USING (EXISTS (
        SELECT 1 FROM identity.users u
        WHERE u.id = user_id
          AND u.tenant_id = current_setting('app.current_tenant', true)::uuid
    ));

COMMIT;
