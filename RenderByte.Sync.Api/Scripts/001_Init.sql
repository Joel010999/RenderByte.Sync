CREATE TABLE organizations (
    id          SERIAL          PRIMARY KEY,
    slug        TEXT            NOT NULL UNIQUE,
    name        TEXT            NOT NULL,
    is_active   BOOLEAN         NOT NULL DEFAULT TRUE,
    created_at  TIMESTAMPTZ     NOT NULL DEFAULT NOW()
);

CREATE TABLE sources (
    id              SERIAL          PRIMARY KEY,
    organization_id INTEGER         NOT NULL REFERENCES organizations(id),
    source_id       TEXT            NOT NULL UNIQUE,
    branch_id       INTEGER         NOT NULL,
    name            TEXT            NOT NULL,
    api_key_hash    TEXT            NOT NULL,
    is_active       BOOLEAN         NOT NULL DEFAULT TRUE,
    created_at      TIMESTAMPTZ     NOT NULL DEFAULT NOW(),
    last_seen_at    TIMESTAMPTZ
);

CREATE INDEX idx_sources_org ON sources(organization_id);

CREATE TABLE stock_movements_raw (
    id              BIGSERIAL       PRIMARY KEY,
    organization_id INTEGER         NOT NULL REFERENCES organizations(id),
    source_id       TEXT            NOT NULL REFERENCES sources(source_id),
    branch_id       INTEGER         NOT NULL,
    movement_key    TEXT            NOT NULL,
    business_key    TEXT            NOT NULL,
    depo            SMALLINT        NOT NULL,
    tipomov         CHAR(2)         NOT NULL,
    fecha           TEXT            NOT NULL,
    codcom          CHAR(4)         NOT NULL,
    ptovta          CHAR(4)         NOT NULL,
    numero          CHAR(8)         NOT NULL,
    proveedor       CHAR(13)        NOT NULL,
    idarti          CHAR(10)        NOT NULL,
    bulto           CHAR(6)         NOT NULL,
    local_          SMALLINT        NOT NULL,
    item            SMALLINT        NOT NULL,
    fedepo          TEXT,
    oferta          INTEGER,
    cantidad        NUMERIC,
    saldo           NUMERIC,
    costo           NUMERIC,
    precio          NUMERIC,
    clave_u         CHAR(10)        NOT NULL,
    piezas          NUMERIC,
    received_at     TIMESTAMPTZ     NOT NULL DEFAULT NOW(),

    CONSTRAINT uq_movement_key UNIQUE (movement_key)
);

CREATE INDEX idx_smr_source_id    ON stock_movements_raw(source_id);
CREATE INDEX idx_smr_fedepo       ON stock_movements_raw(fedepo)       WHERE fedepo IS NOT NULL;
CREATE INDEX idx_smr_business_key ON stock_movements_raw(business_key);
CREATE INDEX idx_smr_received_at  ON stock_movements_raw(received_at);
