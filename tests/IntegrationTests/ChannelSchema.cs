namespace Stay.IntegrationTests;

/// <summary>
/// The channel tables from <c>db/schema.sql</c> plus the ARI calendars the ingest writes to
/// (June–July 2030 partitions, matching <see cref="AriSchema"/>).
/// </summary>
internal static class ChannelSchema
{
    public const string Ddl = AriSchema.Ddl + """

        CREATE SCHEMA IF NOT EXISTS channel;

        CREATE TABLE channel.channel_connection (
            id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            property_id     BIGINT NOT NULL,
            provider        TEXT   NOT NULL,
            credentials_ref TEXT   NOT NULL,
            status          TEXT   NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','PAUSED','ERROR')),
            last_sync_at    TIMESTAMPTZ,
            created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE TABLE channel.room_mapping (
            id                    BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            channel_connection_id BIGINT NOT NULL REFERENCES channel.channel_connection(id),
            external_room_code    TEXT   NOT NULL,
            room_type_id          BIGINT NOT NULL,
            external_rate_code    TEXT,
            rate_plan_id          BIGINT,
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

        CREATE TABLE channel.sync_conflict (
            id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            property_id BIGINT NOT NULL,
            booking_id  BIGINT,
            type        TEXT   NOT NULL,
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
        """;
}
