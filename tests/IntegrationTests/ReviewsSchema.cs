namespace Stay.IntegrationTests;

/// <summary>The reviews tables touched here, from <c>db/schema.sql</c> (review + the reviewable read model).</summary>
internal static class ReviewsSchema
{
    public const string Ddl = """
        CREATE SCHEMA IF NOT EXISTS reviews;

        CREATE TABLE reviews.review (
            id             BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            booking_id     BIGINT      NOT NULL UNIQUE,
            property_id    BIGINT      NOT NULL,
            guest_id       BIGINT      NOT NULL,
            overall_rating SMALLINT    NOT NULL CHECK (overall_rating BETWEEN 1 AND 5),
            sub_scores     JSONB,
            title          TEXT,
            body           TEXT,
            status         TEXT        NOT NULL DEFAULT 'PENDING'
                           CHECK (status IN ('PENDING','PUBLISHED','REJECTED')),
            created_at     TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE TABLE reviews.reviewable_stay (
            booking_id  BIGINT PRIMARY KEY,
            guest_id    BIGINT  NOT NULL,
            property_id BIGINT  NOT NULL,
            reviewed    BOOLEAN NOT NULL DEFAULT false,
            created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE TABLE reviews.property_rating_aggregate (
            property_id    BIGINT PRIMARY KEY,
            review_count   INT    NOT NULL DEFAULT 0,
            avg_overall    NUMERIC(3,2) NOT NULL DEFAULT 0,
            sub_score_avgs JSONB,
            updated_at     TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE TABLE reviews.outbox_message (
            id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            type TEXT NOT NULL, payload JSONB NOT NULL,
            occurred_at TIMESTAMPTZ NOT NULL DEFAULT now(), processed_at TIMESTAMPTZ
        );
        """;
}
