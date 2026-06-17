namespace Stay.IntegrationTests;

/// <summary>
/// The partitioned <c>ari.inventory_calendar</c> from <c>db/schema.sql</c> (incl. the BR-1 CHECK),
/// with explicit partitions covering the fixed test window (June–July 2030).
/// </summary>
internal static class AriSchema
{
    public const string Ddl = """
        CREATE SCHEMA IF NOT EXISTS ari;

        CREATE TABLE ari.inventory_calendar (
            room_type_id        BIGINT   NOT NULL,
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
            CHECK (units_sold + units_held <= total_allotment)
        ) PARTITION BY RANGE (stay_date);

        CREATE TABLE ari.inventory_calendar_2030_06 PARTITION OF ari.inventory_calendar
            FOR VALUES FROM ('2030-06-01') TO ('2030-07-01');
        CREATE TABLE ari.inventory_calendar_2030_07 PARTITION OF ari.inventory_calendar
            FOR VALUES FROM ('2030-07-01') TO ('2030-08-01');

        CREATE TABLE ari.rate_calendar (
            room_type_id     BIGINT        NOT NULL,
            rate_plan_id     BIGINT        NOT NULL,
            stay_date        DATE          NOT NULL,
            base_price       NUMERIC(12,2) NOT NULL CHECK (base_price >= 0),
            currency         CHAR(3)       NOT NULL,
            occupancy_prices JSONB,
            PRIMARY KEY (room_type_id, rate_plan_id, stay_date)
        ) PARTITION BY RANGE (stay_date);

        CREATE TABLE ari.rate_calendar_2030_06 PARTITION OF ari.rate_calendar
            FOR VALUES FROM ('2030-06-01') TO ('2030-07-01');
        CREATE TABLE ari.rate_calendar_2030_07 PARTITION OF ari.rate_calendar
            FOR VALUES FROM ('2030-07-01') TO ('2030-08-01');
        """;
}
