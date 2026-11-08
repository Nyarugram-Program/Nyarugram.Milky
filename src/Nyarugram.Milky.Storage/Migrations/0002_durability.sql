-- Round 2: delivery durability, send/call idempotency and restart reconciliation.
-- Additive only: existing projections stay readable.

-- ---------------------------------------------------------------- outbox

-- Delivery state is explicit. Rows are never silently retired as "delivered";
-- retention moves them to 'expired' so operators can see what was lost.
ALTER TABLE nyarugram_milky.outbox ADD COLUMN IF NOT EXISTS state text NOT NULL DEFAULT 'pending';
ALTER TABLE nyarugram_milky.outbox ADD COLUMN IF NOT EXISTS expired_at timestamptz;
UPDATE nyarugram_milky.outbox SET state = 'delivered' WHERE delivered_at IS NOT NULL AND state = 'pending';

ALTER TABLE nyarugram_milky.outbox DROP CONSTRAINT IF EXISTS outbox_state_check;
ALTER TABLE nyarugram_milky.outbox
    ADD CONSTRAINT outbox_state_check CHECK (state IN ('pending', 'delivered', 'expired'));

CREATE INDEX IF NOT EXISTS outbox_state_idx
    ON nyarugram_milky.outbox(account_id, event_seq) WHERE state = 'pending';

-- Per-consumer replay position. A consumer is identified by the application
-- (nyarugram_consumer) or by an identifier issued at connect time.
CREATE TABLE IF NOT EXISTS nyarugram_milky.event_cursor (
    account_id   uuid        NOT NULL REFERENCES nyarugram_milky.account(account_id) ON DELETE CASCADE,
    consumer_id  text        NOT NULL,
    last_seq     bigint      NOT NULL DEFAULT 0,
    updated_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (account_id, consumer_id),
    CONSTRAINT event_cursor_consumer_len CHECK (length(consumer_id) BETWEEN 1 AND 128)
);

-- -------------------------------------------------------------- messages

-- Messages that could not be represented are recorded rather than silently dropped.
CREATE TABLE IF NOT EXISTS nyarugram_milky.message_skip (
    account_id  uuid        NOT NULL REFERENCES nyarugram_milky.account(account_id) ON DELETE CASCADE,
    message_id  bigint      NOT NULL,
    reason      text        NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (account_id, message_id)
);

-- ---------------------------------------------------------- idempotency

-- A pending operation must be recoverable after a crash: it stores the frozen
-- request identity, the random id and the current stage.
ALTER TABLE nyarugram_milky.idempotency ADD COLUMN IF NOT EXISTS state text NOT NULL DEFAULT 'pending';
ALTER TABLE nyarugram_milky.idempotency ADD COLUMN IF NOT EXISTS attempts integer NOT NULL DEFAULT 0;
ALTER TABLE nyarugram_milky.idempotency ADD COLUMN IF NOT EXISTS last_error text;
ALTER TABLE nyarugram_milky.idempotency ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();
-- The frozen request body makes a blind retry after a crash possible without regenerating
-- anything: the same random id and the same content are replayed verbatim.
ALTER TABLE nyarugram_milky.idempotency ADD COLUMN IF NOT EXISTS request_json jsonb;
ALTER TABLE nyarugram_milky.idempotency ADD COLUMN IF NOT EXISTS lease_until timestamptz;
UPDATE nyarugram_milky.idempotency SET state = 'completed' WHERE completed_at IS NOT NULL AND state = 'pending';

ALTER TABLE nyarugram_milky.idempotency DROP CONSTRAINT IF EXISTS idempotency_state_check;
ALTER TABLE nyarugram_milky.idempotency
    ADD CONSTRAINT idempotency_state_check CHECK (state IN ('pending', 'submitted', 'completed', 'failed', 'unknown'));

-- ------------------------------------------------------------------ calls

-- generation belongs to the login session; instance_id identifies the process that
-- owns the live media engine. A restart changes the instance but not the generation.
ALTER TABLE nyarugram_milky.call ADD COLUMN IF NOT EXISTS instance_id text;
ALTER TABLE nyarugram_milky.call ADD COLUMN IF NOT EXISTS media_version bigint NOT NULL DEFAULT 0;
ALTER TABLE nyarugram_milky.call ADD COLUMN IF NOT EXISTS reconcile_state text;
ALTER TABLE nyarugram_milky.call ADD COLUMN IF NOT EXISTS reconcile_attempts integer NOT NULL DEFAULT 0;
ALTER TABLE nyarugram_milky.call ADD COLUMN IF NOT EXISTS last_error text;

CREATE INDEX IF NOT EXISTS call_reconcile_idx
    ON nyarugram_milky.call(account_id) WHERE ended_at IS NULL;

-- Call start is a multi-step operation (DH material + backend request + projection).
-- Every stage is persisted so a crash cannot lose the random id or the DH secret.
CREATE TABLE IF NOT EXISTS nyarugram_milky.call_operation (
    account_id    uuid        NOT NULL REFERENCES nyarugram_milky.account(account_id) ON DELETE CASCADE,
    idempotency_key text      NOT NULL,
    request_hash  text        NOT NULL,
    random_id     bigint      NOT NULL,
    ga_hash       bytea       NOT NULL,
    secret_cipher bytea       NOT NULL,
    secret_nonce  bytea       NOT NULL,
    secret_tag    bytea       NOT NULL,
    stage         text        NOT NULL,
    call_id       uuid        REFERENCES nyarugram_milky.call(call_id) ON DELETE SET NULL,
    lease_until   timestamptz,
    attempts      integer     NOT NULL DEFAULT 0,
    last_error    text,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (account_id, idempotency_key),
    CONSTRAINT call_operation_stage_check CHECK (stage IN ('prepared', 'submitted', 'linked', 'failed')),
    CONSTRAINT call_operation_random_nonzero CHECK (random_id <> 0)
);

-- Media readiness must advance independently of the signalling state so events can be
-- versioned without colliding on the same call state version.
CREATE TABLE IF NOT EXISTS nyarugram_milky.call_media_event (
    account_id     uuid        NOT NULL REFERENCES nyarugram_milky.account(account_id) ON DELETE CASCADE,
    call_id        uuid        NOT NULL REFERENCES nyarugram_milky.call(call_id) ON DELETE CASCADE,
    media_version  bigint      NOT NULL,
    media_state    text        NOT NULL,
    created_at     timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (account_id, call_id, media_version)
);
