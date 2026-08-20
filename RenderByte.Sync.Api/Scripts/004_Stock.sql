CREATE TABLE stock_raw (
    id              BIGSERIAL       PRIMARY KEY,
    organization_id INTEGER         NOT NULL REFERENCES organizations(id),
    source_id       TEXT            NOT NULL REFERENCES sources(source_id),
    branch_id       INTEGER         NOT NULL,
    article_id      INTEGER         NOT NULL,
    bulto           TEXT            NOT NULL,
    business_key    TEXT            NOT NULL,
    content_hash    TEXT            NOT NULL,
    payload         JSONB           NOT NULL,
    is_present      BOOLEAN         NOT NULL DEFAULT TRUE,
    received_at     TIMESTAMPTZ     NOT NULL DEFAULT NOW(),
    source_seen_at  TIMESTAMPTZ     NOT NULL DEFAULT NOW(),

    CONSTRAINT uq_stock_key UNIQUE (source_id, branch_id, article_id, bulto)
);

CREATE INDEX idx_sr_source_id    ON stock_raw(source_id);
CREATE INDEX idx_sr_business_key ON stock_raw(business_key);
CREATE INDEX idx_sr_received_at  ON stock_raw(received_at);
