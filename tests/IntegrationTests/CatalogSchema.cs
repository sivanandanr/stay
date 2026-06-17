namespace Stay.IntegrationTests;

/// <summary>
/// The catalog tables CreateProperty touches, copied from <c>db/schema.sql</c> (incl. the PostGIS
/// geography column, FKs and CHECKs) so the EF mapping is exercised against the real table shape.
/// Requires a PostGIS-enabled image.
/// </summary>
internal static class CatalogSchema
{
    public const string Ddl = """
        CREATE EXTENSION IF NOT EXISTS postgis;
        CREATE EXTENSION IF NOT EXISTS pgcrypto;
        CREATE SCHEMA IF NOT EXISTS catalog;

        CREATE TABLE catalog.city (
            id            BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            name          TEXT        NOT NULL,
            country_code  CHAR(2)     NOT NULL,
            region        TEXT,
            geo           GEOGRAPHY(POINT, 4326) NOT NULL,
            timezone      TEXT        NOT NULL,
            created_at    TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE TABLE catalog.host (
            id                 BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            identity_sub       TEXT        NOT NULL UNIQUE,
            display_name       TEXT        NOT NULL,
            status             TEXT        NOT NULL DEFAULT 'PENDING'
                               CHECK (status IN ('PENDING','ACTIVE','SUSPENDED')),
            kyc_status         TEXT        NOT NULL DEFAULT 'NOT_STARTED'
                               CHECK (kyc_status IN ('NOT_STARTED','IN_REVIEW','APPROVED','REJECTED')),
            payout_account_ref TEXT,
            tax_info           JSONB,
            created_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
            row_version        INTEGER     NOT NULL DEFAULT 0
        );

        CREATE TABLE catalog.property (
            id               BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            host_id          BIGINT      NOT NULL REFERENCES catalog.host(id),
            name             TEXT        NOT NULL,
            property_type    TEXT        NOT NULL
                             CHECK (property_type IN ('HOTEL','VILLA','APARTMENT','HOMESTAY','RESORT')),
            description      TEXT,
            star_rating      SMALLINT    CHECK (star_rating BETWEEN 1 AND 5),
            status           TEXT        NOT NULL DEFAULT 'DRAFT'
                             CHECK (status IN ('DRAFT','IN_REVIEW','LIVE','SUSPENDED')),
            geo              GEOGRAPHY(POINT, 4326) NOT NULL,
            country_code     CHAR(2)     NOT NULL,
            city_id          BIGINT      NOT NULL REFERENCES catalog.city(id),
            address          JSONB       NOT NULL,
            default_currency CHAR(3)     NOT NULL,
            timezone         TEXT        NOT NULL,
            check_in_time    TIME,
            check_out_time   TIME,
            created_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
            row_version      INTEGER     NOT NULL DEFAULT 0
        );

        CREATE TABLE catalog.room_type (
            id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            property_id     BIGINT      NOT NULL REFERENCES catalog.property(id),
            name            TEXT        NOT NULL,
            unit_kind       TEXT        NOT NULL CHECK (unit_kind IN ('ROOM','ENTIRE_UNIT')),
            total_units     INT         NOT NULL CHECK (total_units >= 0),
            base_occupancy  SMALLINT    NOT NULL CHECK (base_occupancy > 0),
            max_occupancy   SMALLINT    NOT NULL CHECK (max_occupancy >= base_occupancy),
            max_adults      SMALLINT,
            max_children    SMALLINT,
            bed_config      JSONB,
            size_sqm        NUMERIC(6,1),
            row_version     INTEGER     NOT NULL DEFAULT 0
        );

        CREATE TABLE catalog.outbox_message (
            id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            type         TEXT        NOT NULL,
            payload      JSONB       NOT NULL,
            occurred_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
            processed_at TIMESTAMPTZ
        );
        """;
}
