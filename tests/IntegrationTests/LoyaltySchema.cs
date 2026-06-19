namespace Stay.IntegrationTests;

/// <summary>The loyalty tables from <c>db/schema.sql</c> (account + append-only ledger, incl. the non-negative CHECK).</summary>
internal static class LoyaltySchema
{
    public const string Ddl = """
        CREATE SCHEMA IF NOT EXISTS loyalty;

        CREATE TABLE loyalty.account (
            id         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            guest_id   BIGINT NOT NULL UNIQUE,
            balance    INT    NOT NULL DEFAULT 0 CHECK (balance >= 0),
            tier       TEXT   NOT NULL DEFAULT 'BRONZE',
            updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE TABLE loyalty.ledger (
            id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            account_id      BIGINT NOT NULL REFERENCES loyalty.account(id),
            type            TEXT   NOT NULL CHECK (type IN ('EARN','REDEEM','ADJUST')),
            points          INT    NOT NULL,
            reason          TEXT,
            reference       TEXT,
            idempotency_key TEXT   NOT NULL UNIQUE,
            created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """;
}
