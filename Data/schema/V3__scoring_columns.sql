-- Optional: persist computed tier values for debugging/analytics
-- The scoring service computes these at runtime from existing Candidate fields.
-- Adding these columns allows the admin UI to show "why" a stream was selected.
ALTER TABLE stream_candidates ADD COLUMN res_tier    INTEGER;
ALTER TABLE stream_candidates ADD COLUMN src_tier    INTEGER;
ALTER TABLE stream_candidates ADD COLUMN audio_tier  INTEGER;
ALTER TABLE stream_candidates ADD COLUMN display_name TEXT;
