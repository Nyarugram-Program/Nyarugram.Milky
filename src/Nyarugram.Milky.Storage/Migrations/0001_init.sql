-- Nyarugram.Milky adapter state. One independent schema, versioned migrations.
-- Runtime storage is PostgreSQL only; no file database or JSON snapshot bridge.
CREATE SCHEMA IF NOT EXISTS nyarugram_milky;

CREATE TABLE IF NOT EXISTS nyarugram_milky.schema_migration (
    version      integer      PRIMARY KEY,
    name         text         NOT NULL,
    applied_at   timestamptz  NOT NULL DEFAULT now()
);

-- One row per (backend server identity, backend account UUID). The uin is the
-- backend TL user id, which is the stable numeric identity Milky consumes.
CREATE TABLE IF NOT EXISTS nyarugram_milky.account (
    account_id     uuid         PRIMARY KEY DEFAULT gen_random_uuid(),
    server_domain  text         NOT NULL,
    account_uuid   text         NOT NULL,
    uin            bigint       NOT NULL,
    username       text         NOT NULL DEFAULT '',
    display_name   text         NOT NULL DEFAULT '',
    created_at     timestamptz  NOT NULL DEFAULT now(),
    updated_at     timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT account_identity_key UNIQUE (server_domain, account_uuid),
    CONSTRAINT account_uin_key UNIQUE (server_domain, uin),
    CONSTRAINT account_uin_positive CHECK (uin > 0)
);

-- Bearer tokens are encrypted with a key held outside the database. generation is
-- monotonic per account: every in-flight request captures it and results from an
-- older generation are discarded.
CREATE TABLE IF NOT EXISTS nyarugram_milky.session (
    session_id     uuid         PRIMARY KEY DEFAULT gen_random_uuid(),
    account_id     uuid         NOT NULL REFERENCES nyarugram_milky.account(account_id) ON DELETE CASCADE,
    generation     bigint       NOT NULL,
    token_cipher   bytea        NOT NULL,
    token_nonce    bytea        NOT NULL,
    token_tag      bytea        NOT NULL,
    expires_at     timestamptz  NOT NULL,
    created_at     timestamptz  NOT NULL DEFAULT now(),
    revoked_at     timestamptz,
    CONSTRAINT session_generation_key UNIQUE (account_id, generation)
);

CREATE UNIQUE INDEX IF NOT EXISTS session_active_key
    ON nyarugram_milky.session(account_id) WHERE revoked_at IS NULL;

CREATE TABLE IF NOT EXISTS nyarugram_milky.sync_state (
    account_id     uuid         PRIMARY KEY REFERENCES nyarugram_milky.account(account_id) ON DELETE CASCADE,
    pts            integer      NOT NULL DEFAULT 0,
    qts            integer      NOT NULL DEFAULT 0,
    seq            integer      NOT NULL DEFAULT 0,
    date_ms        bigint       NOT NULL DEFAULT 0,
    initialized    boolean      NOT NULL DEFAULT false,
    updated_at     timestamptz  NOT NULL DEFAULT now()
);

-- Numeric aliases stay account-scoped even when the backend TL id could be shared.
CREATE TABLE IF NOT EXISTS nyarugram_milky.id_map (
    account_id     uuid         NOT NULL REFERENCES nyarugram_milky.account(account_id) ON DELETE CASCADE,
    kind           text         NOT NULL CHECK (kind IN ('user', 'group')),
    external_id    text         NOT NULL,
    uin            bigint       NOT NULL,
    updated_at     timestamptz  NOT NULL DEFAULT now(),
    PRIMARY KEY (account_id, kind, external_id),
    CONSTRAINT id_map_uin_key UNIQUE (account_id, kind, uin)
);

-- Compact projection of backend messages. The backend stays authoritative for
-- content; this table only fixes identity and ordering across restarts.
CREATE TABLE IF NOT EXISTS nyarugram_milky.message (
    account_id     uuid         NOT NULL REFERENCES nyarugram_milky.account(account_id) ON DELETE CASCADE,
    message_id     bigint       NOT NULL,
    peer_kind      text         NOT NULL CHECK (peer_kind IN ('friend', 'group')),
    peer_uin       bigint       NOT NULL,
    sender_uin     bigint       NOT NULL,
    created_at_ms  bigint       NOT NULL,
    outgoing       boolean      NOT NULL,
    PRIMARY KEY (account_id, message_id)
);

CREATE INDEX IF NOT EXISTS message_peer_idx
    ON nyarugram_milky.message(account_id, peer_kind, peer_uin, message_id DESC);

-- Idempotent operations. random_id is stored as bigint so the full signed 64-bit
-- range survives; a mismatched payload for the same key is a conflict.
CREATE TABLE IF NOT EXISTS nyarugram_milky.idempotency (
    account_id     uuid         NOT NULL REFERENCES nyarugram_milky.account(account_id) ON DELETE CASCADE,
    scope          text         NOT NULL,
    idempotency_key text        NOT NULL,
    request_hash   text         NOT NULL,
    random_id      bigint,
    result_json    text,
    created_at     timestamptz  NOT NULL DEFAULT now(),
    completed_at   timestamptz,
    PRIMARY KEY (account_id, scope, idempotency_key)
);

-- At-least-once delivery queue. Rows are written in the same transaction as the
-- state change that produced them and deleted only after the transport accepts them.
CREATE TABLE IF NOT EXISTS nyarugram_milky.outbox (
    event_seq      bigserial    PRIMARY KEY,
    account_id     uuid         NOT NULL REFERENCES nyarugram_milky.account(account_id) ON DELETE CASCADE,
    event_id       text         NOT NULL,
    event_type     text         NOT NULL,
    payload        jsonb        NOT NULL,
    created_at     timestamptz  NOT NULL DEFAULT now(),
    delivered_at   timestamptz,
    attempts       integer      NOT NULL DEFAULT 0,
    CONSTRAINT outbox_event_key UNIQUE (account_id, event_id)
);

CREATE INDEX IF NOT EXISTS outbox_pending_idx
    ON nyarugram_milky.outbox(account_id, event_seq) WHERE delivered_at IS NULL;

-- Adapter-side call projection. call_id is an opaque, persisted identifier bound to
-- server, account, login generation and the backend call id.
CREATE TABLE IF NOT EXISTS nyarugram_milky.call (
    call_id        uuid         PRIMARY KEY DEFAULT gen_random_uuid(),
    account_id     uuid         NOT NULL REFERENCES nyarugram_milky.account(account_id) ON DELETE CASCADE,
    generation     bigint       NOT NULL,
    backend_call_id bigint,
    access_hash    bigint,
    direction      text         NOT NULL CHECK (direction IN ('incoming', 'outgoing')),
    peer_uin       bigint       NOT NULL,
    state          text         NOT NULL,
    state_version  bigint       NOT NULL DEFAULT 1,
    video          boolean      NOT NULL DEFAULT false,
    created_at     timestamptz  NOT NULL DEFAULT now(),
    connected_at   timestamptz,
    ended_at       timestamptz,
    end_reason     text,
    media_state    text         NOT NULL DEFAULT 'none',
    media_reason   text,
    -- Outgoing call secret (g^a) / incoming secret (g^b), encrypted with the adapter key.
    secret_cipher  bytea,
    secret_nonce   bytea,
    secret_tag     bytea,
    updated_at     timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT call_backend_key UNIQUE (account_id, backend_call_id)
);

CREATE INDEX IF NOT EXISTS call_active_idx
    ON nyarugram_milky.call(account_id, state) WHERE ended_at IS NULL;

-- Short-lived media capability. Only a hash is stored; the token itself is shown once.
CREATE TABLE IF NOT EXISTS nyarugram_milky.call_media_token (
    token_hash     bytea        PRIMARY KEY,
    call_id        uuid         NOT NULL REFERENCES nyarugram_milky.call(call_id) ON DELETE CASCADE,
    account_id     uuid         NOT NULL REFERENCES nyarugram_milky.account(account_id) ON DELETE CASCADE,
    expires_at     timestamptz  NOT NULL,
    created_at     timestamptz  NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS call_media_token_call_idx
    ON nyarugram_milky.call_media_token(call_id);
