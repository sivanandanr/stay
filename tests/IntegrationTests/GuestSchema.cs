namespace Stay.IntegrationTests;

/// <summary>The <c>guest.guest_profile</c> table, from <c>db/schema.sql</c>.</summary>
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
            row_version          INTEGER     NOT NULL DEFAULT 0
        );
        """;
}
