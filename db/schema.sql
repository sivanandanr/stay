-- ============================================================================
-- Hotel / Stay / Villa Booking Platform — PostgreSQL schema
-- Schema-per-bounded-context (database-per-context logical separation).
--
-- CONVENTIONS
--  * One PostgreSQL schema per bounded context. During the modular-monolith
--    phase these live in one physical database; each context owns its own
--    EF Core migration history and may be extracted to its own database later.
--  * REAL foreign keys are declared ONLY within a context. Cross-context
--    references are plain columns (commented "xref:") with NO FK, so a context
--    can be split out without a dangling constraint. Integrity across contexts
--    is enforced by domain logic + events, not by the database.
--  * Identity is EXTERNAL: users are referenced by `identity_sub` (the OIDC
--    `sub` claim). No credentials/passwords are stored here.
--  * Money: NUMERIC(12,2) + explicit currency CHAR(3). Never float.
--  * Time: TIMESTAMPTZ everywhere; stay-night math uses DATE in the property
--    timezone (BR-4).
--  * Concurrency: row_version INTEGER as an optimistic token (EF Core
--    [ConcurrencyCheck]); the ARI hot path additionally uses conditional
--    UPDATEs (see notes at the inventory_calendar table).
-- ============================================================================

CREATE EXTENSION IF NOT EXISTS postgis;     -- geography type for geo search
CREATE EXTENSION IF NOT EXISTS pgcrypto;    -- gen_random_uuid()

CREATE SCHEMA IF NOT EXISTS catalog;
CREATE SCHEMA IF NOT EXISTS ari;
CREATE SCHEMA IF NOT EXISTS pricing;
CREATE SCHEMA IF NOT EXISTS guest;
CREATE SCHEMA IF NOT EXISTS booking;
CREATE SCHEMA IF NOT EXISTS payment;
CREATE SCHEMA IF NOT EXISTS reviews;
CREATE SCHEMA IF NOT EXISTS promotion;
CREATE SCHEMA IF NOT EXISTS channel;
CREATE SCHEMA IF NOT EXISTS admin;
CREATE SCHEMA IF NOT EXISTS notify;

-- A reusable outbox template is created per writing context (see each schema).

-- ============================================================================
-- CONTEXT: catalog  (properties, units, amenities, media, geo master data)
-- ============================================================================

CREATE TABLE catalog.city (
    id            BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name          TEXT        NOT NULL,
    country_code  CHAR(2)     NOT NULL,
    region        TEXT,
    geo           GEOGRAPHY(POINT, 4326) NOT NULL,
    timezone      TEXT        NOT NULL,                  -- IANA, e.g. Asia/Singapore
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_city_country ON catalog.city (country_code);
CREATE INDEX idx_city_geo     ON catalog.city USING GIST (geo);

-- Host: platform-owned record keyed by the external identity sub.
-- KYC state is platform-owned (FRD OQ-1 recommendation); Identity stays generic.
CREATE TABLE catalog.host (
    id                 BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    identity_sub       TEXT        NOT NULL UNIQUE,       -- xref: Identity Service
    display_name       TEXT        NOT NULL,
    status             TEXT        NOT NULL DEFAULT 'PENDING'  -- PENDING|ACTIVE|SUSPENDED
                       CHECK (status IN ('PENDING','ACTIVE','SUSPENDED')),
    kyc_status         TEXT        NOT NULL DEFAULT 'NOT_STARTED' -- NOT_STARTED|IN_REVIEW|APPROVED|REJECTED
                       CHECK (kyc_status IN ('NOT_STARTED','IN_REVIEW','APPROVED','REJECTED')),
    payout_account_ref TEXT,                              -- token at payout provider
    tax_info           JSONB,
    created_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
    row_version        INTEGER     NOT NULL DEFAULT 0
);

CREATE TABLE catalog.property (
    id               BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    host_id          BIGINT      NOT NULL REFERENCES catalog.host(id),
    name             TEXT        NOT NULL,
    property_type    TEXT        NOT NULL                 -- HOTEL|VILLA|APARTMENT|HOMESTAY|RESORT
                     CHECK (property_type IN ('HOTEL','VILLA','APARTMENT','HOMESTAY','RESORT')),
    description      TEXT,
    star_rating      SMALLINT    CHECK (star_rating BETWEEN 1 AND 5),
    status           TEXT        NOT NULL DEFAULT 'DRAFT' -- DRAFT|IN_REVIEW|LIVE|SUSPENDED
                     CHECK (status IN ('DRAFT','IN_REVIEW','LIVE','SUSPENDED')),
    geo              GEOGRAPHY(POINT, 4326) NOT NULL,
    country_code     CHAR(2)     NOT NULL,
    city_id          BIGINT      NOT NULL REFERENCES catalog.city(id),
    address          JSONB       NOT NULL,
    default_currency CHAR(3)     NOT NULL,
    timezone         TEXT        NOT NULL,                -- IANA; drives BR-4 math
    check_in_time    TIME,
    check_out_time   TIME,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    row_version      INTEGER     NOT NULL DEFAULT 0
);
CREATE INDEX idx_property_geo      ON catalog.property USING GIST (geo);
CREATE INDEX idx_property_city     ON catalog.property (city_id) WHERE status = 'LIVE';
CREATE INDEX idx_property_host     ON catalog.property (host_id);
CREATE INDEX idx_property_live     ON catalog.property (status) WHERE status = 'LIVE';

CREATE TABLE catalog.room_type (
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    property_id     BIGINT      NOT NULL REFERENCES catalog.property(id),
    name            TEXT        NOT NULL,
    unit_kind       TEXT        NOT NULL                  -- ROOM | ENTIRE_UNIT (villa)
                    CHECK (unit_kind IN ('ROOM','ENTIRE_UNIT')),
    total_units     INT         NOT NULL CHECK (total_units >= 0),
    base_occupancy  SMALLINT    NOT NULL CHECK (base_occupancy > 0),
    max_occupancy   SMALLINT    NOT NULL CHECK (max_occupancy >= base_occupancy),
    max_adults      SMALLINT,
    max_children    SMALLINT,
    bed_config      JSONB,
    size_sqm        NUMERIC(6,1),
    row_version     INTEGER     NOT NULL DEFAULT 0
);
CREATE INDEX idx_room_type_property ON catalog.room_type (property_id);

CREATE TABLE catalog.amenity (
    id        BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code      TEXT NOT NULL UNIQUE,
    category  TEXT NOT NULL,                              -- e.g. CONNECTIVITY, KITCHEN, POOL
    label     TEXT NOT NULL
);

CREATE TABLE catalog.property_amenity (
    property_id BIGINT NOT NULL REFERENCES catalog.property(id),
    amenity_id  BIGINT NOT NULL REFERENCES catalog.amenity(id),
    PRIMARY KEY (property_id, amenity_id)
);

CREATE TABLE catalog.room_type_amenity (
    room_type_id BIGINT NOT NULL REFERENCES catalog.room_type(id),
    amenity_id   BIGINT NOT NULL REFERENCES catalog.amenity(id),
    PRIMARY KEY (room_type_id, amenity_id)
);

CREATE TABLE catalog.media (
    id           BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    property_id  BIGINT      REFERENCES catalog.property(id),
    room_type_id BIGINT      REFERENCES catalog.room_type(id),
    storage_key  TEXT        NOT NULL,                    -- object-store key
    kind         TEXT        NOT NULL CHECK (kind IN ('IMAGE','VIDEO','THREE_SIXTY')),
    alt_text     TEXT,
    sort_order   INT         NOT NULL DEFAULT 0,
    is_primary   BOOLEAN     NOT NULL DEFAULT false,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    CHECK (property_id IS NOT NULL OR room_type_id IS NOT NULL)
);
CREATE INDEX idx_media_property ON catalog.media (property_id);

-- House rules / property-level policies (cancellation policies live in ari).
CREATE TABLE catalog.property_policy (
    property_id     BIGINT PRIMARY KEY REFERENCES catalog.property(id),
    house_rules     JSONB,
    child_policy    JSONB,
    pet_policy      JSONB,
    quiet_hours     JSONB,
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    row_version     INTEGER NOT NULL DEFAULT 0
);

-- Outbox (catalog)
CREATE TABLE catalog.outbox_message (
    id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    type         TEXT        NOT NULL,
    payload      JSONB       NOT NULL,
    occurred_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    processed_at TIMESTAMPTZ
);
CREATE INDEX idx_catalog_outbox_unprocessed ON catalog.outbox_message (occurred_at) WHERE processed_at IS NULL;

-- ============================================================================
-- CONTEXT: ari  (availability, rates, inventory, rate plans, cancel policy)
-- The two calendar tables are the highest-volume, highest-contention tables.
-- ============================================================================

CREATE TABLE ari.cancellation_policy (
    id            BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    property_id   BIGINT      NOT NULL,                   -- xref: catalog.property
    name          TEXT        NOT NULL,
    -- tiers: [{"hours_before_checkin":48,"refund_pct":100},{"hours_before_checkin":24,"refund_pct":50},...]
    tiers         JSONB       NOT NULL,
    is_refundable BOOLEAN     NOT NULL DEFAULT true,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    row_version   INTEGER     NOT NULL DEFAULT 0
);

CREATE TABLE ari.rate_plan (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    property_id            BIGINT  NOT NULL,              -- xref: catalog.property
    name                   TEXT    NOT NULL,
    meal_plan              TEXT    CHECK (meal_plan IN ('ROOM_ONLY','BREAKFAST','HALF_BOARD','FULL_BOARD','ALL_INCLUSIVE')),
    cancellation_policy_id BIGINT  NOT NULL REFERENCES ari.cancellation_policy(id),
    is_refundable          BOOLEAN NOT NULL DEFAULT true,
    status                 TEXT    NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','INACTIVE')),
    row_version            INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX idx_rate_plan_property ON ari.rate_plan (property_id);

-- INVENTORY: one row per room_type per night. Range-partitioned by month.
-- available(night) = total_allotment - units_sold - units_held
-- HOLD path (BR-1) uses a single conditional UPDATE over the night range with a
-- row-count check = all-nights-or-none. PK MUST include the partition key.
CREATE TABLE ari.inventory_calendar (
    room_type_id        BIGINT   NOT NULL,               -- xref: catalog.room_type
    stay_date           DATE     NOT NULL,
    total_allotment     INT      NOT NULL DEFAULT 0 CHECK (total_allotment >= 0),
    units_sold          INT      NOT NULL DEFAULT 0 CHECK (units_sold >= 0),
    units_held          INT      NOT NULL DEFAULT 0 CHECK (units_held >= 0),
    stop_sell           BOOLEAN  NOT NULL DEFAULT false,
    min_los             SMALLINT,
    max_los             SMALLINT,
    closed_to_arrival   BOOLEAN  NOT NULL DEFAULT false,
    closed_to_departure BOOLEAN  NOT NULL DEFAULT false,
    row_version         INTEGER  NOT NULL DEFAULT 0,
    PRIMARY KEY (room_type_id, stay_date),
    CHECK (units_sold + units_held <= total_allotment)   -- BR-1 invariant, enforced by the DB
) PARTITION BY RANGE (stay_date);

-- RATES: per room_type / rate_plan / night. Partitioned the same way.
CREATE TABLE ari.rate_calendar (
    room_type_id     BIGINT        NOT NULL,             -- xref: catalog.room_type
    rate_plan_id     BIGINT        NOT NULL,             -- xref: ari.rate_plan (same context; no FK on partitioned table)
    stay_date        DATE          NOT NULL,
    base_price       NUMERIC(12,2) NOT NULL CHECK (base_price >= 0),
    currency         CHAR(3)       NOT NULL,
    occupancy_prices JSONB,                              -- {"1": -20.00, "3": 15.00}
    PRIMARY KEY (room_type_id, rate_plan_id, stay_date)
) PARTITION BY RANGE (stay_date);

-- Monthly partition automation: create_calendar_partitions(months_ahead).
-- In production use pg_partman or a scheduled job; this proves the syntax and
-- seeds an initial window.
CREATE OR REPLACE FUNCTION ari.create_calendar_partitions(p_from DATE, p_months INT)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    m_start DATE;
    m_end   DATE;
    suffix  TEXT;
BEGIN
    FOR i IN 0..(p_months - 1) LOOP
        m_start := date_trunc('month', p_from) + (i || ' month')::interval;
        m_end   := m_start + interval '1 month';
        suffix  := to_char(m_start, 'YYYY_MM');

        EXECUTE format(
            'CREATE TABLE IF NOT EXISTS ari.inventory_calendar_%s
                PARTITION OF ari.inventory_calendar
                FOR VALUES FROM (%L) TO (%L);', suffix, m_start, m_end);

        EXECUTE format(
            'CREATE TABLE IF NOT EXISTS ari.rate_calendar_%s
                PARTITION OF ari.rate_calendar
                FOR VALUES FROM (%L) TO (%L);', suffix, m_start, m_end);
    END LOOP;
END;
$$;

-- Seed ~18 months of partitions from the start of the current month.
SELECT ari.create_calendar_partitions(date_trunc('month', current_date)::date, 18);

CREATE TABLE ari.outbox_message (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    type TEXT NOT NULL, payload JSONB NOT NULL,
    occurred_at TIMESTAMPTZ NOT NULL DEFAULT now(), processed_at TIMESTAMPTZ
);
CREATE INDEX idx_ari_outbox_unprocessed ON ari.outbox_message (occurred_at) WHERE processed_at IS NULL;

-- ============================================================================
-- CONTEXT: pricing  (rules-as-data, taxes/fees, FX snapshots)
-- Stateless/deterministic compute; tables hold the rule + reference data only.
-- ============================================================================

CREATE TABLE pricing.pricing_rule (
    id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    scope_type  TEXT    NOT NULL CHECK (scope_type IN ('PROPERTY','ROOM_TYPE','RATE_PLAN')),
    scope_id    BIGINT  NOT NULL,                         -- xref: catalog/ari id per scope_type
    rule_type   TEXT    NOT NULL CHECK (rule_type IN ('LOS_DISCOUNT','EARLY_BIRD','LAST_MINUTE','SEASONAL','OCCUPANCY')),
    priority    INT     NOT NULL DEFAULT 100,
    conditions  JSONB   NOT NULL,                         -- {"min_nights":7}
    effect      JSONB   NOT NULL,                         -- {"discount_pct":10}
    valid_from  DATE,
    valid_to    DATE,
    active      BOOLEAN NOT NULL DEFAULT true
);
CREATE INDEX idx_pricing_rule_scope ON pricing.pricing_rule (scope_type, scope_id) WHERE active;

CREATE TABLE pricing.tax_fee_rule (
    id           BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    country_code CHAR(2) NOT NULL,
    city_id      BIGINT,                                  -- xref: catalog.city (nullable = country-wide)
    name         TEXT    NOT NULL,
    kind         TEXT    NOT NULL CHECK (kind IN ('TAX','RESORT_FEE','CLEANING_FEE','SERVICE_FEE')),
    calc_type    TEXT    NOT NULL CHECK (calc_type IN ('PERCENT','FIXED_PER_NIGHT','FIXED_PER_STAY','FIXED_PER_PERSON')),
    rate         NUMERIC(10,4) NOT NULL,
    applies_to   TEXT    NOT NULL DEFAULT 'BASE' CHECK (applies_to IN ('BASE','BASE_PLUS_FEES')),
    valid_from   DATE,
    valid_to     DATE,
    active       BOOLEAN NOT NULL DEFAULT true
);
CREATE INDEX idx_tax_fee_jurisdiction ON pricing.tax_fee_rule (country_code, city_id) WHERE active;

CREATE TABLE pricing.fx_rate (
    base_currency  CHAR(3) NOT NULL,
    quote_currency CHAR(3) NOT NULL,
    rate           NUMERIC(18,8) NOT NULL,
    as_of          TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (base_currency, quote_currency, as_of)
);

-- ============================================================================
-- CONTEXT: guest  (booking profile keyed by identity sub; travelers, wishlist,
--                  saved PSP tokens, loyalty). NEVER stores credentials.
-- ============================================================================

CREATE TABLE guest.guest_profile (
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    identity_sub        TEXT        NOT NULL UNIQUE,       -- xref: Identity Service
    email_cache         TEXT,                              -- cache of claim; not mastered here
    name_cache          TEXT,
    email_verified_cache BOOLEAN    NOT NULL DEFAULT false,
    locale              TEXT,
    preferred_currency  CHAR(3),
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    row_version         INTEGER     NOT NULL DEFAULT 0
);

CREATE TABLE guest.saved_traveler (
    id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    guest_id    BIGINT NOT NULL REFERENCES guest.guest_profile(id),
    full_name   TEXT   NOT NULL,
    date_of_birth DATE,
    nationality CHAR(2),
    document    JSONB,                                     -- passport/id (consider app-level encryption)
    is_default  BOOLEAN NOT NULL DEFAULT false
);
CREATE INDEX idx_traveler_guest ON guest.saved_traveler (guest_id);

CREATE TABLE guest.wishlist_item (
    guest_id    BIGINT NOT NULL REFERENCES guest.guest_profile(id),
    property_id BIGINT NOT NULL,                           -- xref: catalog.property
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (guest_id, property_id)
);

CREATE TABLE guest.payment_method_token (
    id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    guest_id    BIGINT NOT NULL REFERENCES guest.guest_profile(id),
    psp         TEXT   NOT NULL,
    token       TEXT   NOT NULL,                           -- PSP token only; NO PAN
    brand       TEXT,
    last4       CHAR(4),
    exp_month   SMALLINT,
    exp_year    SMALLINT,
    is_default  BOOLEAN NOT NULL DEFAULT false,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_pm_token_guest ON guest.payment_method_token (guest_id);

CREATE TABLE guest.loyalty_account (
    guest_id      BIGINT PRIMARY KEY REFERENCES guest.guest_profile(id),
    points_balance BIGINT NOT NULL DEFAULT 0 CHECK (points_balance >= 0),
    tier          TEXT   NOT NULL DEFAULT 'BASE',
    updated_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    row_version   INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE guest.loyalty_ledger (
    id         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    guest_id   BIGINT NOT NULL REFERENCES guest.guest_profile(id),
    delta      BIGINT NOT NULL,                            -- +earn / -redeem
    reason     TEXT   NOT NULL,
    booking_id BIGINT,                                     -- xref: booking.booking
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_loyalty_ledger_guest ON guest.loyalty_ledger (guest_id);

-- ============================================================================
-- CONTEXT: booking  (the transactional core: cart/hold/reservation, saga)
-- ============================================================================

CREATE TABLE booking.booking (
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    reference       TEXT        NOT NULL UNIQUE,           -- human-facing, checksummed
    guest_id        BIGINT      NOT NULL,                  -- xref: guest.guest_profile; login required, no guest checkout
    contact_email   TEXT        NOT NULL,                  -- snapshot for this booking (defaults from profile)
    contact_phone   TEXT,
    property_id     BIGINT      NOT NULL,                  -- xref: catalog.property
    status          TEXT        NOT NULL DEFAULT 'DRAFT'
                    CHECK (status IN ('DRAFT','HELD','CONFIRMED','CANCELLED','EXPIRED','NO_SHOW','COMPLETED','FAILED')),
    currency        CHAR(3)     NOT NULL,
    room_subtotal   NUMERIC(12,2) NOT NULL DEFAULT 0,
    tax_amount      NUMERIC(12,2) NOT NULL DEFAULT 0,
    fees_amount     NUMERIC(12,2) NOT NULL DEFAULT 0,
    total_amount    NUMERIC(12,2) NOT NULL DEFAULT 0,
    hold_expires_at TIMESTAMPTZ,                           -- set while HELD (BR-3)
    source          TEXT        NOT NULL DEFAULT 'WEB' CHECK (source IN ('WEB','MOBILE','PARTNER')),
    partner_id      BIGINT,                                -- xref: admin/partner when source=PARTNER
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    row_version     INTEGER     NOT NULL DEFAULT 0
);
CREATE INDEX idx_booking_guest    ON booking.booking (guest_id);
CREATE INDEX idx_booking_property ON booking.booking (property_id);
CREATE INDEX idx_booking_status   ON booking.booking (status);
CREATE INDEX idx_booking_hold_exp ON booking.booking (hold_expires_at) WHERE status = 'HELD';

CREATE TABLE booking.booking_room (
    id                BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    booking_id        BIGINT      NOT NULL REFERENCES booking.booking(id) ON DELETE CASCADE,
    room_type_id      BIGINT      NOT NULL,                -- xref: catalog.room_type
    rate_plan_id      BIGINT      NOT NULL,                -- xref: ari.rate_plan
    check_in          DATE        NOT NULL,
    check_out         DATE        NOT NULL,                -- nights = [check_in, check_out)
    quantity          INT         NOT NULL CHECK (quantity > 0),
    adults            SMALLINT    NOT NULL DEFAULT 1,
    children          SMALLINT    NOT NULL DEFAULT 0,
    nightly_breakdown JSONB       NOT NULL,                -- frozen quote (BR-2)
    subtotal          NUMERIC(12,2) NOT NULL,
    tax_amount        NUMERIC(12,2) NOT NULL DEFAULT 0,
    status            TEXT        NOT NULL DEFAULT 'ACTIVE' -- ACTIVE|CANCELLED (partial cancel)
                      CHECK (status IN ('ACTIVE','CANCELLED')),
    CHECK (check_out > check_in)
);
CREATE INDEX idx_booking_room_booking ON booking.booking_room (booking_id);

CREATE TABLE booking.booking_guest (
    id         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    booking_id BIGINT  NOT NULL REFERENCES booking.booking(id) ON DELETE CASCADE,
    full_name  TEXT    NOT NULL,
    is_lead    BOOLEAN NOT NULL DEFAULT false
);

CREATE TABLE booking.special_request (
    id         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    booking_id BIGINT NOT NULL REFERENCES booking.booking(id) ON DELETE CASCADE,
    code       TEXT,                                       -- LATE_CHECKIN, HIGH_FLOOR...
    note       TEXT
);

-- Transient holds for the expiry reaper + audit (BR-3). The fast availability
-- counter lives on inventory_calendar; this table drives release/reconciliation.
CREATE TABLE booking.inventory_hold (
    id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    booking_id   BIGINT NOT NULL REFERENCES booking.booking(id) ON DELETE CASCADE,
    room_type_id BIGINT NOT NULL,                          -- xref: catalog.room_type
    stay_date    DATE   NOT NULL,
    quantity     INT    NOT NULL CHECK (quantity > 0),
    expires_at   TIMESTAMPTZ NOT NULL,
    released     BOOLEAN NOT NULL DEFAULT false
);
CREATE INDEX idx_hold_expiry  ON booking.inventory_hold (expires_at) WHERE released = false;
CREATE INDEX idx_hold_booking ON booking.inventory_hold (booking_id);

CREATE TABLE booking.cancellation (
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    booking_id      BIGINT  NOT NULL REFERENCES booking.booking(id),
    booking_room_id BIGINT  REFERENCES booking.booking_room(id),  -- NULL = whole booking
    reason          TEXT,
    refund_amount   NUMERIC(12,2) NOT NULL DEFAULT 0,
    refund_currency CHAR(3),
    policy_snapshot JSONB   NOT NULL,                       -- the policy evaluated (BR-7 audit)
    initiated_by    TEXT    NOT NULL CHECK (initiated_by IN ('GUEST','HOST','OPS','SYSTEM')),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_cancellation_booking ON booking.cancellation (booking_id);

CREATE TABLE booking.status_history (
    id         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    booking_id BIGINT NOT NULL REFERENCES booking.booking(id),
    from_status TEXT,
    to_status   TEXT NOT NULL,
    at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    note        TEXT
);
CREATE INDEX idx_status_history_booking ON booking.status_history (booking_id);

-- Saga orchestration state (hold -> auth -> commit -> capture -> confirm).
CREATE TABLE booking.saga_state (
    id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    booking_id   BIGINT NOT NULL REFERENCES booking.booking(id),
    saga_type    TEXT   NOT NULL DEFAULT 'BOOKING',
    current_step TEXT   NOT NULL,
    state        JSONB  NOT NULL DEFAULT '{}',
    is_complete  BOOLEAN NOT NULL DEFAULT false,
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    row_version  INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX idx_saga_open ON booking.saga_state (updated_at) WHERE is_complete = false;

CREATE TABLE booking.outbox_message (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    type TEXT NOT NULL, payload JSONB NOT NULL,
    occurred_at TIMESTAMPTZ NOT NULL DEFAULT now(), processed_at TIMESTAMPTZ
);
CREATE INDEX idx_booking_outbox_unprocessed ON booking.outbox_message (occurred_at) WHERE processed_at IS NULL;

-- ============================================================================
-- CONTEXT: payment  (gateway abstraction, refunds, webhooks, host payouts)
-- ============================================================================

CREATE TABLE payment.payment (
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    booking_id      BIGINT      NOT NULL,                  -- xref: booking.booking
    psp             TEXT        NOT NULL,                  -- STRIPE|ADYEN|RAZORPAY
    psp_ref         TEXT,                                  -- gateway charge/intent id
    intent_id       TEXT,
    amount          NUMERIC(12,2) NOT NULL CHECK (amount >= 0),
    currency        CHAR(3)     NOT NULL,
    fx_rate_used    NUMERIC(18,8),
    status          TEXT        NOT NULL DEFAULT 'PENDING'
                    CHECK (status IN ('PENDING','AUTHORIZED','CAPTURED','FAILED','VOIDED','REFUNDED','PARTIALLY_REFUNDED')),
    idempotency_key TEXT        NOT NULL UNIQUE,           -- BR-5
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    row_version     INTEGER     NOT NULL DEFAULT 0
);
CREATE INDEX idx_payment_booking ON payment.payment (booking_id);
CREATE INDEX idx_payment_status  ON payment.payment (status);

CREATE TABLE payment.refund (
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    payment_id      BIGINT      NOT NULL REFERENCES payment.payment(id),
    amount          NUMERIC(12,2) NOT NULL CHECK (amount > 0),
    currency        CHAR(3)     NOT NULL,
    reason          TEXT,
    status          TEXT        NOT NULL DEFAULT 'PENDING'
                    CHECK (status IN ('PENDING','SUCCEEDED','FAILED')),
    psp_ref         TEXT,
    idempotency_key TEXT        NOT NULL UNIQUE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_refund_payment ON payment.refund (payment_id);

-- Idempotent webhook ingestion: source of truth for async PSP state.
CREATE TABLE payment.webhook_event (
    id           BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    psp          TEXT NOT NULL,
    psp_event_id TEXT NOT NULL,
    type         TEXT NOT NULL,
    payload      JSONB NOT NULL,
    received_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    processed_at TIMESTAMPTZ,
    UNIQUE (psp, psp_event_id)
);

CREATE TABLE payment.payout (
    id            BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    host_id       BIGINT      NOT NULL,                    -- xref: catalog.host
    period_start  DATE        NOT NULL,
    period_end    DATE        NOT NULL,
    gross_amount  NUMERIC(14,2) NOT NULL,
    commission    NUMERIC(14,2) NOT NULL,
    net_amount    NUMERIC(14,2) NOT NULL,
    currency      CHAR(3)     NOT NULL,
    status        TEXT        NOT NULL DEFAULT 'DRAFT'
                  CHECK (status IN ('DRAFT','SCHEDULED','PAID','FAILED')),
    statement_ref TEXT,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_payout_host ON payment.payout (host_id);

CREATE TABLE payment.payout_line (
    id         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    payout_id  BIGINT NOT NULL REFERENCES payment.payout(id),
    booking_id BIGINT NOT NULL,                            -- xref: booking.booking
    gross      NUMERIC(12,2) NOT NULL,
    commission NUMERIC(12,2) NOT NULL,
    net        NUMERIC(12,2) NOT NULL
);
CREATE INDEX idx_payout_line_payout ON payment.payout_line (payout_id);

CREATE TABLE payment.outbox_message (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    type TEXT NOT NULL, payload JSONB NOT NULL,
    occurred_at TIMESTAMPTZ NOT NULL DEFAULT now(), processed_at TIMESTAMPTZ
);
CREATE INDEX idx_payment_outbox_unprocessed ON payment.outbox_message (occurred_at) WHERE processed_at IS NULL;

-- ============================================================================
-- CONTEXT: reviews  (verified post-stay reviews, responses, aggregates)
-- ============================================================================

CREATE TABLE reviews.review (
    id            BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    booking_id    BIGINT      NOT NULL UNIQUE,             -- xref: booking.booking (BR-6: one per stay)
    property_id   BIGINT      NOT NULL,                    -- xref: catalog.property
    guest_id      BIGINT      NOT NULL,                    -- xref: guest.guest_profile
    overall_rating SMALLINT   NOT NULL CHECK (overall_rating BETWEEN 1 AND 5),
    sub_scores    JSONB,                                   -- {"cleanliness":5,"location":4,...}
    title         TEXT,
    body          TEXT,
    status        TEXT        NOT NULL DEFAULT 'PENDING'
                  CHECK (status IN ('PENDING','PUBLISHED','REJECTED')),
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_review_property ON reviews.review (property_id) WHERE status = 'PUBLISHED';

CREATE TABLE reviews.review_response (
    id        BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    review_id BIGINT NOT NULL UNIQUE REFERENCES reviews.review(id),
    host_id   BIGINT NOT NULL,                             -- xref: catalog.host
    body      TEXT   NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE reviews.review_flag (
    id         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    review_id  BIGINT NOT NULL REFERENCES reviews.review(id),
    reporter   TEXT   NOT NULL,                            -- identity sub or 'GUEST'/'HOST'
    reason     TEXT   NOT NULL,
    status     TEXT   NOT NULL DEFAULT 'OPEN' CHECK (status IN ('OPEN','REVIEWED','DISMISSED')),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Materialized aggregate, refreshed on ReviewPublished events.
CREATE TABLE reviews.property_rating_aggregate (
    property_id    BIGINT PRIMARY KEY,                     -- xref: catalog.property
    review_count   INT    NOT NULL DEFAULT 0,
    avg_overall    NUMERIC(3,2) NOT NULL DEFAULT 0,
    sub_score_avgs JSONB,
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- ============================================================================
-- CONTEXT: promotion  (platform & host promotions, coupons, redemptions)
-- ============================================================================

CREATE TABLE promotion.promotion (
    id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    owner_type  TEXT    NOT NULL CHECK (owner_type IN ('PLATFORM','HOST')),
    owner_id    BIGINT,                                    -- xref: catalog.host when HOST
    name        TEXT    NOT NULL,
    type        TEXT    NOT NULL CHECK (type IN ('PERCENT_OFF','FIXED_OFF','FREE_NIGHT')),
    conditions  JSONB   NOT NULL,
    effect      JSONB   NOT NULL,
    valid_from  TIMESTAMPTZ,
    valid_to    TIMESTAMPTZ,
    budget      NUMERIC(14,2),
    status      TEXT    NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('DRAFT','ACTIVE','PAUSED','ENDED'))
);

CREATE TABLE promotion.coupon (
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    promotion_id    BIGINT NOT NULL REFERENCES promotion.promotion(id),
    code            TEXT   NOT NULL UNIQUE,
    max_redemptions INT,
    per_user_limit  INT    NOT NULL DEFAULT 1,
    redeemed_count  INT    NOT NULL DEFAULT 0,
    status          TEXT   NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','DISABLED'))
);

CREATE TABLE promotion.coupon_redemption (
    id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    coupon_id   BIGINT NOT NULL REFERENCES promotion.coupon(id),
    booking_id  BIGINT NOT NULL,                           -- xref: booking.booking
    guest_id    BIGINT,                                    -- xref: guest.guest_profile
    amount      NUMERIC(12,2) NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (coupon_id, booking_id)
);

-- ============================================================================
-- CONTEXT: channel  (channel manager / PMS connections, mappings, sync log)
-- ============================================================================

CREATE TABLE channel.channel_connection (
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    property_id     BIGINT NOT NULL,                       -- xref: catalog.property
    provider        TEXT   NOT NULL,                       -- SITEMINDER|STAAH|...
    credentials_ref TEXT   NOT NULL,                       -- secret store ref
    status          TEXT   NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','PAUSED','ERROR')),
    last_sync_at    TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_channel_property ON channel.channel_connection (property_id);

CREATE TABLE channel.room_mapping (
    id                    BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    channel_connection_id BIGINT NOT NULL REFERENCES channel.channel_connection(id),
    external_room_code    TEXT   NOT NULL,
    room_type_id          BIGINT NOT NULL,                 -- xref: catalog.room_type
    external_rate_code    TEXT,
    rate_plan_id          BIGINT,                          -- xref: ari.rate_plan
    UNIQUE (channel_connection_id, external_room_code, external_rate_code)
);

CREATE TABLE channel.ari_sync_log (
    id                    BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    channel_connection_id BIGINT NOT NULL REFERENCES channel.channel_connection(id),
    direction             TEXT   NOT NULL CHECK (direction IN ('INBOUND','OUTBOUND')),
    message_seq           BIGINT,
    payload_hash          TEXT,
    status                TEXT   NOT NULL CHECK (status IN ('APPLIED','DROPPED_STALE','QUARANTINED','ERROR')),
    detail                TEXT,
    received_at           TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_sync_log_conn ON channel.ari_sync_log (channel_connection_id, received_at);

CREATE TABLE channel.sync_conflict (
    id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    property_id BIGINT NOT NULL,                           -- xref: catalog.property
    booking_id  BIGINT,                                    -- xref: booking.booking
    type        TEXT   NOT NULL,                           -- OVERBOOK|RATE_MISMATCH|...
    detail      JSONB,
    status      TEXT   NOT NULL DEFAULT 'OPEN' CHECK (status IN ('OPEN','RESOLVED','ESCALATED')),
    resolution  TEXT,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE channel.outbox_message (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    type TEXT NOT NULL, payload JSONB NOT NULL,
    occurred_at TIMESTAMPTZ NOT NULL DEFAULT now(), processed_at TIMESTAMPTZ
);

-- ============================================================================
-- CONTEXT: admin  (platform roles mapped from identity, audit, fraud, partners)
-- ============================================================================

CREATE TABLE admin.platform_role (
    id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code        TEXT NOT NULL UNIQUE,                      -- guest|host|ops|moderator|finance|partner
    description TEXT
);

CREATE TABLE admin.role_assignment (
    id           BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    identity_sub TEXT   NOT NULL,                          -- xref: Identity Service
    role_id      BIGINT NOT NULL REFERENCES admin.platform_role(id),
    scope_type   TEXT,                                     -- NULL=global, or 'PROPERTY'
    scope_id     BIGINT,
    granted_by   TEXT,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (identity_sub, role_id, scope_type, scope_id)
);
CREATE INDEX idx_role_assignment_sub ON admin.role_assignment (identity_sub);

CREATE TABLE admin.partner (
    id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name        TEXT NOT NULL,
    client_id   TEXT NOT NULL UNIQUE,                      -- issued by Identity (client-credentials)
    commission_pct NUMERIC(5,2) NOT NULL DEFAULT 0,
    markup_pct  NUMERIC(5,2) NOT NULL DEFAULT 0,
    status      TEXT NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','SUSPENDED')),
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Immutable audit trail of privileged actions (BR-7).
CREATE TABLE admin.audit_log (
    id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    actor_sub   TEXT   NOT NULL,
    action      TEXT   NOT NULL,
    entity_type TEXT   NOT NULL,
    entity_id   TEXT   NOT NULL,
    before      JSONB,
    after       JSONB,
    reason      TEXT,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_audit_entity ON admin.audit_log (entity_type, entity_id);
CREATE INDEX idx_audit_actor  ON admin.audit_log (actor_sub, created_at);

CREATE TABLE admin.block_list (
    id         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    kind       TEXT NOT NULL CHECK (kind IN ('EMAIL','IP','CARD_FINGERPRINT','DEVICE')),
    value      TEXT NOT NULL,
    reason     TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at TIMESTAMPTZ,
    UNIQUE (kind, value)
);

-- ============================================================================
-- CONTEXT: notify  (NotificationAdapter emission ledger — delivery is external)
-- ============================================================================

CREATE TABLE notify.notification_emission (
    id         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    event_id   TEXT   NOT NULL UNIQUE,                     -- BR-5 idempotency key
    category   TEXT   NOT NULL,                            -- booking_confirmed, host_new_booking...
    recipient  TEXT   NOT NULL,                            -- identity sub or raw contact (guest checkout)
    locale     TEXT,
    payload    JSONB  NOT NULL,
    transport  TEXT   NOT NULL,
    status     TEXT   NOT NULL DEFAULT 'EMITTED' CHECK (status IN ('EMITTED','FAILED')),
    emitted_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_emission_category ON notify.notification_emission (category, emitted_at);
