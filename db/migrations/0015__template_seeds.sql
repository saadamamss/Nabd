-- 0015__template_seeds.sql — P2 spec 04 (FR-13 channels-as-config, FR-15 cold-chain
-- preset, acceptance #9: a new vertical = INSERT rows, no code migration). Global
-- templates (0002's shape: no tenant_id — per-tenant overrides deferred, NOTES).
-- LIMITS ARE PRESETS, NOT RULES: P4's engine seeds per-rule from them; nothing in
-- P2 evaluates these numbers. Idempotent: rebuilds and re-runs stay green.

INSERT INTO assets.asset_type_templates (key, name_ar, name_en) VALUES
    ('fridge', 'تخزين بارد', 'Cold Storage'),
    ('tank',   'خزان ماء',   'Water Tank')
ON CONFLICT (key) DO NOTHING;

-- channel rows join their template BY KEY (no hard-coded uuids in seeds, ever)
INSERT INTO assets.channel_definitions
    (template_id, key, name_ar, name_en, unit, data_type, lower_limit, upper_limit)
SELECT t.id, c.key, c.name_ar, c.name_en, c.unit, c.dtype, c.lo, c.hi
FROM (VALUES
    ('fridge', 'temperature', 'حرارة',      'Temperature', '°C',    'number', 2.0::double precision, 8.0::double precision),
    ('fridge', 'door',        'حالة الباب', 'Door',        'state', 'bool',   NULL::double precision,  NULL::double precision),
    ('tank',   'level',       'المستوى',    'Level',       '%',     'number', 0.0::double precision, 100.0::double precision),
    ('tank',   'flow',        'التدفق',     'Flow',        'L/min', 'number', NULL::double precision,  NULL::double precision)
) AS c(tpl, key, name_ar, name_en, unit, dtype, lo, hi)
JOIN assets.asset_type_templates t ON t.key = c.tpl
ON CONFLICT (template_id, key) DO NOTHING;