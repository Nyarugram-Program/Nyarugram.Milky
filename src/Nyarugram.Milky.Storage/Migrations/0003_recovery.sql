-- Round 3: protocol verification, media recovery, hangup reconciliation and lease ownership.
-- Additive only; existing rows keep working with NULL/default values.

-- ---------------------------------------------------------------- calls

-- The caller's commitment (SHA-256 of g_a) as first observed. Later updates must never
-- overwrite it: the callee compares the eventual g_a against this exact value.
ALTER TABLE nyarugram_milky.call ADD COLUMN IF NOT EXISTS peer_ga_hash bytea;
-- Provenance of the shared key: unverified, verified (hash + range + fingerprint), failed.
ALTER TABLE nyarugram_milky.call ADD COLUMN IF NOT EXISTS key_state text NOT NULL DEFAULT 'none';
ALTER TABLE nyarugram_milky.call ADD COLUMN IF NOT EXISTS key_verified_at timestamptz;

-- Accept material is persisted before accept_call so a lost response or a restart can
-- resubmit the same g_b instead of generating a new key the peer never saw.
ALTER TABLE nyarugram_milky.call ADD COLUMN IF NOT EXISTS accept_secret_cipher bytea;
ALTER TABLE nyarugram_milky.call ADD COLUMN IF NOT EXISTS accept_secret_nonce bytea;
ALTER TABLE nyarugram_milky.call ADD COLUMN IF NOT EXISTS accept_secret_tag bytea;
ALTER TABLE nyarugram_milky.call ADD COLUMN IF NOT EXISTS accept_gb bytea;
ALTER TABLE nyarugram_milky.call ADD COLUMN IF NOT EXISTS accept_stage text;
ALTER TABLE nyarugram_milky.call ADD COLUMN IF NOT EXISTS accept_request_id text;

-- Media is recovered independently from the signalling state.
ALTER TABLE nyarugram_milky.call ADD COLUMN IF NOT EXISTS media_stage text NOT NULL DEFAULT 'none';
ALTER TABLE nyarugram_milky.call ADD COLUMN IF NOT EXISTS media_attempts integer NOT NULL DEFAULT 0;
ALTER TABLE nyarugram_milky.call ADD COLUMN IF NOT EXISTS media_next_attempt_at timestamptz;
ALTER TABLE nyarugram_milky.call ADD COLUMN IF NOT EXISTS media_last_error text;

-- Latest backend PhoneCall snapshot. Media recovery and hangup reconciliation run after a
-- restart, when the original difference page is no longer available.
ALTER TABLE nyarugram_milky.call ADD COLUMN IF NOT EXISTS last_call_blob bytea;

-- A hangup whose backend outcome is unknown stays here until the backend confirms it.
ALTER TABLE nyarugram_milky.call ADD COLUMN IF NOT EXISTS pending_end_reason text;
ALTER TABLE nyarugram_milky.call ADD COLUMN IF NOT EXISTS pending_end_at timestamptz;
ALTER TABLE nyarugram_milky.call ADD COLUMN IF NOT EXISTS reconcile_next_at timestamptz;

ALTER TABLE nyarugram_milky.call DROP CONSTRAINT IF EXISTS call_accept_stage_check;
ALTER TABLE nyarugram_milky.call
    ADD CONSTRAINT call_accept_stage_check
    CHECK (accept_stage IS NULL OR accept_stage IN ('prepared', 'submitted', 'accepted'));

ALTER TABLE nyarugram_milky.call DROP CONSTRAINT IF EXISTS call_media_stage_check;
ALTER TABLE nyarugram_milky.call
    ADD CONSTRAINT call_media_stage_check
    CHECK (media_stage IN ('none', 'pending', 'ready', 'failed', 'closed'));

ALTER TABLE nyarugram_milky.call DROP CONSTRAINT IF EXISTS call_key_state_check;
ALTER TABLE nyarugram_milky.call
    ADD CONSTRAINT call_key_state_check
    CHECK (key_state IN ('none', 'unverified', 'verified', 'failed'));

CREATE INDEX IF NOT EXISTS call_media_recovery_idx
    ON nyarugram_milky.call(account_id, media_next_attempt_at) WHERE ended_at IS NULL;

CREATE INDEX IF NOT EXISTS call_pending_end_idx
    ON nyarugram_milky.call(account_id) WHERE pending_end_reason IS NOT NULL AND ended_at IS NULL;

-- ---------------------------------------------------------- lease ownership

-- A lease is only valid for the executor that owns this token. Renewal, release and
-- completion are conditional on it, so an expired holder can never overwrite the new one.
ALTER TABLE nyarugram_milky.idempotency ADD COLUMN IF NOT EXISTS lease_token uuid;
ALTER TABLE nyarugram_milky.call_operation ADD COLUMN IF NOT EXISTS lease_token uuid;
