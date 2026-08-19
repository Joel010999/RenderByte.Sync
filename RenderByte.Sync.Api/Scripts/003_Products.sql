CREATE TABLE products_raw (
    id              BIGSERIAL       PRIMARY KEY,
    organization_id INTEGER         NOT NULL REFERENCES organizations(id),
    source_id       TEXT            NOT NULL REFERENCES sources(source_id),
    branch_id       INTEGER         NOT NULL,
    article_id      INTEGER         NOT NULL,
    business_key    TEXT            NOT NULL,
    content_hash    TEXT            NOT NULL,
    payload         JSONB           NOT NULL,
    is_present      BOOLEAN         NOT NULL DEFAULT TRUE,
    received_at     TIMESTAMPTZ     NOT NULL DEFAULT NOW(),
    source_seen_at  TIMESTAMPTZ     NOT NULL DEFAULT NOW(),

    CONSTRAINT uq_product_key UNIQUE (source_id, article_id)
);

CREATE INDEX idx_pr_source_id    ON products_raw(source_id);
CREATE INDEX idx_pr_business_key ON products_raw(business_key);
CREATE INDEX idx_pr_received_at  ON products_raw(received_at);
