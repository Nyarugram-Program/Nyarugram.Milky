-- Consumer cursor generation. A resume/rewind bumps the epoch so a late acknowledgement or
-- a stale connection's shutdown write-back cannot overwrite the rewound position.
ALTER TABLE nyarugram_milky.event_cursor ADD COLUMN IF NOT EXISTS epoch bigint NOT NULL DEFAULT 0;
