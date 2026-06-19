namespace Stay.IntegrationTests;

/// <summary>The guest tables the provisioning + erasure flows touch, from <c>db/schema.sql</c>.</summary>
internal static class GuestSchema
{
    public const string Ddl = """
        CREATE SCHEMA IF NOT EXISTS guest;

        CREATE TABLE guest.guest_profile (
            id                   BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            identity_sub         TEXT        NOT NULL UNIQUE,
            email_cache          TEXT,
            name_cache           TEXT,
            email_verified_cache BOOLEAN     NOT NULL DEFAULT false,
            locale               TEXT,
            preferred_currency   CHAR(3),
            created_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
            erased_at            TIMESTAMPTZ,
            row_version          INTEGER     NOT NULL DEFAULT 0
        );

        CREATE TABLE guest.saved_traveler (
            id            BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            guest_id      BIGINT NOT NULL REFERENCES guest.guest_profile(id),
            full_name     TEXT   NOT NULL,
            date_of_birth DATE,
            nationality   CHAR(2),
            document      JSONB,
            is_default    BOOLEAN NOT NULL DEFAULT false
        );

        CREATE TABLE guest.payment_method_token (
            id         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            guest_id   BIGINT NOT NULL REFERENCES guest.guest_profile(id),
            psp        TEXT   NOT NULL,
            token      TEXT   NOT NULL,
            brand      TEXT,
            last4      CHAR(4),
            exp_month  SMALLINT,
            exp_year   SMALLINT,
            is_default BOOLEAN NOT NULL DEFAULT false,
            created_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE TABLE guest.outbox_message (
            id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            type         TEXT        NOT NULL,
            payload      JSONB       NOT NULL,
            occurred_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
            processed_at TIMESTAMPTZ
        );
        """;
}
