-- 0001__identity.sql — tenants + users (plan/04 §4)
CREATE EXTENSION IF NOT EXISTS citext;

CREATE SCHEMA IF NOT EXISTS identity;

CREATE TABLE IF NOT EXISTS identity.tenants (
    id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name       text NOT NULL,
    locale     text NOT NULL DEFAULT 'ar',
    is_active  boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS identity.users (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id     uuid NOT NULL REFERENCES identity.tenants(id),
    email         citext NOT NULL UNIQUE,
    display_name  text NOT NULL,
    role          text NOT NULL CHECK (role IN ('owner','operator','viewer')),
    locale        text NOT NULL DEFAULT 'ar',
    password_hash text,                       -- NULL until P1 (no auth in P0)
    created_at    timestamptz NOT NULL DEFAULT now()
);
