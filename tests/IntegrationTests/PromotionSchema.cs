namespace Stay.IntegrationTests;

/// <summary>The promotion tables from <c>db/schema.sql</c> (promotion + coupon + redemption ledger).</summary>
internal static class PromotionSchema
{
    public const string Ddl = """
        CREATE SCHEMA IF NOT EXISTS promotion;

        CREATE TABLE promotion.promotion (
            id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            owner_type  TEXT    NOT NULL CHECK (owner_type IN ('PLATFORM','HOST')),
            owner_id    BIGINT,
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
            booking_id  BIGINT NOT NULL,
            guest_id    BIGINT,
            amount      NUMERIC(12,2) NOT NULL,
            created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
            UNIQUE (coupon_id, booking_id)
        );
        """;
}
