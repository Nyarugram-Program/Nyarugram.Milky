-- Versioned Web-management overrides. Deployment secrets stay outside PostgreSQL;
-- the optional Milky token override is encrypted with the existing adapter key.
CREATE TABLE IF NOT EXISTS nyarugram_milky.admin_config (
    singleton                    integer     PRIMARY KEY DEFAULT 1 CHECK (singleton = 1),
    version                      bigint      NOT NULL DEFAULT 0,
    sync_interval_seconds        integer,
    outbox_retention_hours       integer,
    profile_cache_ttl_minutes    integer,
    auto_answer                  boolean,
    access_token_cipher          bytea,
    access_token_nonce           bytea,
    access_token_tag             bytea,
    updated_at                   timestamptz NOT NULL DEFAULT now()
);
