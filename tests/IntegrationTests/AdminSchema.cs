namespace Stay.IntegrationTests;

/// <summary>The admin tables the audit projection touches, from <c>db/schema.sql</c>.</summary>
internal static class AdminSchema
{
    public const string Ddl = """
        CREATE SCHEMA IF NOT EXISTS admin;

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

        CREATE TABLE admin.processed_event (
            event_id     TEXT PRIMARY KEY,
            processed_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE TABLE admin.platform_role (
            id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            code        TEXT NOT NULL UNIQUE,
            description TEXT
        );

        CREATE TABLE admin.role_assignment (
            id           BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            identity_sub TEXT   NOT NULL,
            role_id      BIGINT NOT NULL REFERENCES admin.platform_role(id),
            scope_type   TEXT,
            scope_id     BIGINT,
            granted_by   TEXT,
            created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
            UNIQUE (identity_sub, role_id, scope_type, scope_id)
        );

        INSERT INTO admin.platform_role (code) VALUES ('ops'), ('finance'), ('moderator'), ('host');

        CREATE TABLE admin.partner (
            id             BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            name           TEXT NOT NULL,
            client_id      TEXT NOT NULL UNIQUE,
            commission_pct NUMERIC(5,2) NOT NULL DEFAULT 0,
            markup_pct     NUMERIC(5,2) NOT NULL DEFAULT 0,
            status         TEXT NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','SUSPENDED')),
            created_at     TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """;
}
