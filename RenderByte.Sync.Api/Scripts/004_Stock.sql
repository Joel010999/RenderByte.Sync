CREATE TABLE stock_levels_raw (
    id BIGSERIAL PRIMARY KEY,
    organization_id INTEGER NOT NULL REFERENCES organizations(id),
    source_id TEXT NOT NULL REFERENCES sources(source_id),
    branch_id INTEGER NOT NULL,
    depo INTEGER NOT NULL,
    article_id INTEGER NOT NULL,
    bulto TEXT NOT NULL,
    business_key TEXT NOT NULL,
    content_hash TEXT NOT NULL,

    costo NUMERIC(20,5),
    precio NUMERIC(20,5),
    saldo NUMERIC(20,3),
    piezas NUMERIC(6,1),

    is_present BOOLEAN NOT NULL DEFAULT TRUE,
    received_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    source_seen_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT uq_stock_source_identity
        UNIQUE (source_id, depo, article_id, bulto)
);

CREATE INDEX idx_slr_source_id ON stock_levels_raw(source_id);
CREATE INDEX idx_slr_source_article ON stock_levels_raw(source_id, article_id);
CREATE INDEX idx_slr_received_at ON stock_levels_raw(received_at);
