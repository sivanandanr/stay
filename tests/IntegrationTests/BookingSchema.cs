namespace Stay.IntegrationTests;

/// <summary>
/// The booking tables the hold saga writes, from <c>db/schema.sql</c> (incl. the idempotency-key
/// unique constraint). Applied alongside <see cref="AriSchema"/> — one database holds both contexts.
/// </summary>
internal static class BookingSchema
{
    public const string Ddl = """
        CREATE SCHEMA IF NOT EXISTS booking;

        CREATE TABLE booking.booking (
            id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            reference       TEXT        NOT NULL UNIQUE,
            idempotency_key TEXT        NOT NULL UNIQUE,
            guest_id        BIGINT      NOT NULL,
            contact_email   TEXT        NOT NULL,
            contact_phone   TEXT,
            property_id     BIGINT      NOT NULL,
            status          TEXT        NOT NULL DEFAULT 'DRAFT'
                            CHECK (status IN ('DRAFT','HELD','CONFIRMED','CANCELLED','EXPIRED','NO_SHOW','COMPLETED','FAILED')),
            currency        CHAR(3)     NOT NULL,
            room_subtotal   NUMERIC(12,2) NOT NULL DEFAULT 0,
            tax_amount      NUMERIC(12,2) NOT NULL DEFAULT 0,
            fees_amount     NUMERIC(12,2) NOT NULL DEFAULT 0,
            total_amount    NUMERIC(12,2) NOT NULL DEFAULT 0,
            hold_expires_at TIMESTAMPTZ,
            cancellation_snapshot JSONB,
            source          TEXT        NOT NULL DEFAULT 'WEB' CHECK (source IN ('WEB','MOBILE','PARTNER')),
            partner_id      BIGINT,
            created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
            row_version     INTEGER     NOT NULL DEFAULT 0
        );

        CREATE TABLE booking.booking_room (
            id                BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            booking_id        BIGINT      NOT NULL REFERENCES booking.booking(id) ON DELETE CASCADE,
            room_type_id      BIGINT      NOT NULL,
            rate_plan_id      BIGINT      NOT NULL,
            check_in          DATE        NOT NULL,
            check_out         DATE        NOT NULL,
            quantity          INT         NOT NULL CHECK (quantity > 0),
            adults            SMALLINT    NOT NULL DEFAULT 1,
            children          SMALLINT    NOT NULL DEFAULT 0,
            nightly_breakdown JSONB       NOT NULL,
            subtotal          NUMERIC(12,2) NOT NULL,
            tax_amount        NUMERIC(12,2) NOT NULL DEFAULT 0,
            status            TEXT        NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','CANCELLED')),
            CHECK (check_out > check_in)
        );

        CREATE TABLE booking.inventory_hold (
            id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            booking_id   BIGINT NOT NULL REFERENCES booking.booking(id) ON DELETE CASCADE,
            room_type_id BIGINT NOT NULL,
            stay_date    DATE   NOT NULL,
            quantity     INT    NOT NULL CHECK (quantity > 0),
            expires_at   TIMESTAMPTZ NOT NULL,
            released     BOOLEAN NOT NULL DEFAULT false
        );

        CREATE TABLE booking.cancellation (
            id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            booking_id      BIGINT  NOT NULL REFERENCES booking.booking(id),
            booking_room_id BIGINT  REFERENCES booking.booking_room(id),
            reason          TEXT,
            refund_amount   NUMERIC(12,2) NOT NULL DEFAULT 0,
            refund_currency CHAR(3),
            policy_snapshot JSONB   NOT NULL,
            initiated_by    TEXT    NOT NULL CHECK (initiated_by IN ('GUEST','HOST','OPS','SYSTEM')),
            created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE TABLE booking.status_history (
            id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            booking_id  BIGINT NOT NULL REFERENCES booking.booking(id),
            from_status TEXT,
            to_status   TEXT NOT NULL,
            at          TIMESTAMPTZ NOT NULL DEFAULT now(),
            note        TEXT
        );

        CREATE TABLE booking.outbox_message (
            id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            type         TEXT        NOT NULL,
            payload      JSONB       NOT NULL,
            occurred_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
            processed_at TIMESTAMPTZ
        );

        CREATE TABLE booking.reminder_log (
            booking_id    BIGINT NOT NULL REFERENCES booking.booking(id) ON DELETE CASCADE,
            reminder_type TEXT   NOT NULL,
            emitted_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
            PRIMARY KEY (booking_id, reminder_type)
        );
        """;
}
