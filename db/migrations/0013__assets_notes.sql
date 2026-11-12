-- 0013__assets_notes.sql — P2 spec 02 drift fix: the contracts (AssetSnapshot.Notes)
-- assume a `notes` column, but the frozen 0002 never created one. Migrations are
-- append-only — 0002 stays untouched; this migration supplies the missing column.
-- RLS untouched: the column lives on wall-protected rows; the table-level GRANTs
-- from 0012 cover new columns automatically.

ALTER TABLE assets.assets ADD COLUMN IF NOT EXISTS notes text;
