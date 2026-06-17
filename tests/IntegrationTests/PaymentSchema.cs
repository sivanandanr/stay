namespace Stay.IntegrationTests;

/// <summary>The <c>payment.payment</c> table the confirm saga records into, from <c>db/schema.sql</c>.</summary>
internal static class PaymentSchema
{
    public const string Ddl = """
        CREATE SCHEMA IF NOT EXISTS payment;

        CREATE TABLE payment.payment (
            id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            booking_id      BIGINT      NOT NULL,
            psp             TEXT        NOT NULL,
            psp_ref         TEXT,
            intent_id       TEXT,
            amount          NUMERIC(12,2) NOT NULL CHECK (amount >= 0),
            currency        CHAR(3)     NOT NULL,
            fx_rate_used    NUMERIC(18,8),
            status          TEXT        NOT NULL DEFAULT 'PENDING'
                            CHECK (status IN ('PENDING','AUTHORIZED','CAPTURED','FAILED','VOIDED','REFUNDED','PARTIALLY_REFUNDED')),
            idempotency_key TEXT        NOT NULL UNIQUE,
            created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
            row_version     INTEGER     NOT NULL DEFAULT 0
        );

        CREATE TABLE payment.refund (
            id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            payment_id      BIGINT      NOT NULL REFERENCES payment.payment(id),
            amount          NUMERIC(12,2) NOT NULL CHECK (amount > 0),
            currency        CHAR(3)     NOT NULL,
            reason          TEXT,
            status          TEXT        NOT NULL DEFAULT 'PENDING' CHECK (status IN ('PENDING','SUCCEEDED','FAILED')),
            psp_ref         TEXT,
            idempotency_key TEXT        NOT NULL UNIQUE,
            created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
        );

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
            host_id       BIGINT      NOT NULL,
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

        CREATE TABLE payment.payout_line (
            id         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            payout_id  BIGINT NOT NULL REFERENCES payment.payout(id),
            booking_id BIGINT NOT NULL,
            gross      NUMERIC(12,2) NOT NULL,
            commission NUMERIC(12,2) NOT NULL,
            net        NUMERIC(12,2) NOT NULL
        );

        CREATE TABLE payment.outbox_message (
            id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            type TEXT NOT NULL, payload JSONB NOT NULL,
            occurred_at TIMESTAMPTZ NOT NULL DEFAULT now(), processed_at TIMESTAMPTZ
        );
        """;
}
