using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Stay.Search.Infrastructure;

namespace Stay.IntegrationTests;

/// <summary>The guest property search read model against a real OpenSearch instance (Gate G3 surface).</summary>
public sealed class PropertySearchTests : IAsyncLifetime
{
    private readonly IContainer _opensearch = new ContainerBuilder("opensearchproject/opensearch:2.13.0")
        .WithEnvironment("discovery.type", "single-node")
        .WithEnvironment("DISABLE_SECURITY_PLUGIN", "true")
        .WithEnvironment("OPENSEARCH_JAVA_OPTS", "-Xms512m -Xmx512m")
        .WithPortBinding(9200, true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(r => r.ForPort(9200).ForPath("/_cluster/health")))
        .Build();

    private OpenSearchPropertyIndex _index = null!;

    public async Task InitializeAsync()
    {
        await _opensearch.StartAsync();
        var http = new HttpClient
        {
            BaseAddress = new Uri($"http://{_opensearch.Hostname}:{_opensearch.GetMappedPublicPort(9200)}")
        };
        _index = new OpenSearchPropertyIndex(http);
        await _index.EnsureIndexAsync();
    }

    public Task DisposeAsync() => _opensearch.DisposeAsync().AsTask();

    private static PropertySearchDocument Doc(
        long id, string name, string city, string country = "SG", string status = "LIVE", decimal? price = 100m,
        string type = "HOTEL", double? rating = null, IReadOnlyList<string>? amenities = null,
        double lat = 1.29, double lng = 103.85, int popularity = 0) =>
        new(id, name, city, country, type, lat, lng, price, status, Rating: rating, Amenities: amenities, Popularity: popularity);

    private Task<SearchResults> Search(string? city = null, string? country = null, string? text = null, int page = 0, int size = 20) =>
        _index.SearchAsync(new PropertySearchQuery(city, country, text, page, size));

    [Fact]
    public async Task Indexed_live_property_is_found_by_city()
    {
        await _index.IndexAsync(Doc(1, "Marina Bay Suites", "Singapore"));

        var results = await Search(city: "Singapore");

        Assert.Equal(1, results.Total);
        Assert.Equal("Marina Bay Suites", results.Items[0].Name);
        Assert.Equal(100m, results.Items[0].FromPrice);
    }

    [Fact]
    public async Task Non_live_properties_are_excluded()
    {
        await _index.IndexAsync(Doc(1, "Live Hotel", "Bangkok", status: "LIVE"));
        await _index.IndexAsync(Doc(2, "Draft Hotel", "Bangkok", status: "DRAFT"));

        var results = await Search(city: "Bangkok");

        Assert.Equal(1, results.Total);
        Assert.Equal("Live Hotel", results.Items[0].Name);
    }

    [Fact]
    public async Task Search_filters_by_country_and_matches_name_text()
    {
        await _index.IndexAsync(Doc(1, "Beach Resort", "Phuket", country: "TH"));
        await _index.IndexAsync(Doc(2, "City Hostel", "Phuket", country: "TH"));
        await _index.IndexAsync(Doc(3, "Beach Resort", "Bali", country: "ID"));

        var results = await Search(country: "TH", text: "beach");

        Assert.Equal(1, results.Total);
        Assert.Equal(1, results.Items[0].Id);
    }

    [Fact]
    public async Task Results_are_paginated()
    {
        for (var i = 1; i <= 3; i++)
            await _index.IndexAsync(Doc(i, $"Hotel {i}", "Tokyo", country: "JP"));

        var page1 = await Search(city: "Tokyo", page: 0, size: 2);
        var page2 = await Search(city: "Tokyo", page: 1, size: 2);

        Assert.Equal(3, page1.Total);   // total reflects all matches
        Assert.Equal(2, page1.Items.Count);
        Assert.Single(page2.Items);     // remainder on the second page
    }

    [Fact]
    public async Task No_match_returns_empty()
    {
        await _index.IndexAsync(Doc(1, "Somewhere", "Osaka", country: "JP"));

        var results = await Search(city: "Nowhere");

        Assert.Equal(0, results.Total);
        Assert.Empty(results.Items);
    }

    [Fact]
    public async Task Filters_by_price_range()
    {
        await _index.IndexAsync(Doc(1, "Budget Inn", "Delhi", country: "IN", price: 1500m));
        await _index.IndexAsync(Doc(2, "Mid Stay", "Delhi", country: "IN", price: 4000m));
        await _index.IndexAsync(Doc(3, "Luxury Suites", "Delhi", country: "IN", price: 12000m));

        var results = await _index.SearchAsync(
            new PropertySearchQuery("Delhi", null, null, 0, 20, MinPrice: 2000m, MaxPrice: 6000m));

        Assert.Equal(1, results.Total);
        Assert.Equal(2, results.Items[0].Id);
    }

    [Fact]
    public async Task Filters_by_minimum_rating()
    {
        await _index.IndexAsync(Doc(1, "Okay Place", "Pune", country: "IN", rating: 3.4));
        await _index.IndexAsync(Doc(2, "Great Place", "Pune", country: "IN", rating: 4.6));

        var results = await _index.SearchAsync(
            new PropertySearchQuery("Pune", null, null, 0, 20, MinRating: 4.0));

        Assert.Equal(1, results.Total);
        Assert.Equal("Great Place", results.Items[0].Name);
        Assert.Equal(4.6, results.Items[0].Rating);
    }

    [Fact]
    public async Task Requires_all_listed_amenities()
    {
        await _index.IndexAsync(Doc(1, "Pool Only", "Jaipur", country: "IN", amenities: ["POOL"]));
        await _index.IndexAsync(Doc(2, "Pool And Wifi", "Jaipur", country: "IN", amenities: ["POOL", "WIFI", "PARKING"]));

        var results = await _index.SearchAsync(
            new PropertySearchQuery("Jaipur", null, null, 0, 20, Amenities: ["POOL", "WIFI"]));

        Assert.Equal(1, results.Total);
        Assert.Equal(2, results.Items[0].Id);
        Assert.Contains("WIFI", results.Items[0].Amenities!);
    }

    [Fact]
    public async Task Filters_by_property_type()
    {
        await _index.IndexAsync(Doc(1, "Sea Villa", "Goa", country: "IN", type: "VILLA"));
        await _index.IndexAsync(Doc(2, "Grand Hotel", "Goa", country: "IN", type: "HOTEL"));

        var results = await _index.SearchAsync(
            new PropertySearchQuery("Goa", null, null, 0, 20, PropertyType: "VILLA"));

        Assert.Equal(1, results.Total);
        Assert.Equal("Sea Villa", results.Items[0].Name);
    }

    [Fact]
    public async Task Sorts_by_price_ascending_and_descending()
    {
        await _index.IndexAsync(Doc(1, "Mid", "Agra", country: "IN", price: 5000m));
        await _index.IndexAsync(Doc(2, "Cheap", "Agra", country: "IN", price: 1000m));
        await _index.IndexAsync(Doc(3, "Pricey", "Agra", country: "IN", price: 9000m));

        var asc = await _index.SearchAsync(new PropertySearchQuery("Agra", null, null, 0, 20, Sort: SearchSort.PriceAsc));
        var desc = await _index.SearchAsync(new PropertySearchQuery("Agra", null, null, 0, 20, Sort: SearchSort.PriceDesc));

        Assert.Equal(new long[] { 2, 1, 3 }, asc.Items.Select(i => i.Id).ToArray());
        Assert.Equal(new long[] { 3, 1, 2 }, desc.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task Sorts_by_rating_descending()
    {
        await _index.IndexAsync(Doc(1, "Three", "Kochi", country: "IN", rating: 3.0));
        await _index.IndexAsync(Doc(2, "Five", "Kochi", country: "IN", rating: 4.9));
        await _index.IndexAsync(Doc(3, "Four", "Kochi", country: "IN", rating: 4.1));

        var results = await _index.SearchAsync(new PropertySearchQuery("Kochi", null, null, 0, 20, Sort: SearchSort.Rating));

        Assert.Equal(new long[] { 2, 3, 1 }, results.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task Trending_orders_live_properties_by_popularity()
    {
        await _index.IndexAsync(Doc(1, "Quiet Inn", "Lisbon", country: "PT", popularity: 2));
        await _index.IndexAsync(Doc(2, "Hot Hotel", "Lisbon", country: "PT", popularity: 9));
        await _index.IndexAsync(Doc(3, "Draft Hotel", "Lisbon", country: "PT", status: "DRAFT", popularity: 99));

        var trending = await _index.GetTrendingAsync(10);

        Assert.Equal(new long[] { 2, 1 }, trending.Items.Select(i => i.Id).ToArray()); // DRAFT excluded, by popularity desc
    }

    [Fact]
    public async Task IncrementPopularity_bumps_the_score()
    {
        await _index.IndexAsync(Doc(1, "Risers", "Madrid", country: "ES", popularity: 0));

        await _index.IncrementPopularityAsync(1);
        await _index.IncrementPopularityAsync(1);

        var trending = await _index.GetTrendingAsync(10);
        Assert.Equal(2, trending.Items.Single(i => i.Id == 1).Popularity);
    }

    [Fact]
    public async Task PopularityProjection_increments_on_a_confirmed_booking()
    {
        await _index.IndexAsync(Doc(7, "Booked Often", "Rome", country: "IT", popularity: 0));
        var projection = new PopularityProjection(_index);
        var @event = new Stay.Booking.Contracts.BookingConfirmed(
            Guid.NewGuid(), BookingId: 500, "STAY-X", DateTimeOffset.UtcNow, PropertyId: 7);
        var envelope = new Stay.BuildingBlocks.Outbox.OutboxEnvelope(
            @event.EventId, @event.EventType, System.Text.Json.JsonSerializer.Serialize(@event), DateTimeOffset.UtcNow);

        await projection.ProjectAsync(envelope);

        var trending = await _index.GetTrendingAsync(10);
        Assert.Equal(1, trending.Items.Single(i => i.Id == 7).Popularity);
    }

    [Fact]
    public async Task UpdateFromPrice_keeps_the_minimum_lead_in_price()
    {
        await _index.IndexAsync(Doc(1, "Pricey", "Lisbon", country: "PT", price: null)); // no price yet

        await _index.UpdateFromPriceAsync(1, 5000m, "INR");
        await _index.UpdateFromPriceAsync(1, 3000m, "INR"); // lower → wins
        await _index.UpdateFromPriceAsync(1, 8000m, "INR"); // higher → ignored

        var doc = (await _index.GetTrendingAsync(10)).Items.Single(i => i.Id == 1);
        Assert.Equal(3000m, doc.FromPrice);
        Assert.Equal("INR", doc.Currency.Trim());
    }

    [Fact]
    public async Task SearchPriceProjection_lowers_the_from_price_on_a_channel_price_event()
    {
        await _index.IndexAsync(Doc(7, "Tracked", "Porto", country: "PT", price: null));
        var projection = new SearchPriceProjection(_index);
        var @event = new Stay.Channel.Contracts.PropertyPriceChanged(
            Guid.NewGuid(), PropertyId: 7, FromPrice: 2200m, "INR", DateTimeOffset.UtcNow);
        var envelope = new Stay.BuildingBlocks.Outbox.OutboxEnvelope(
            @event.EventId, @event.EventType, System.Text.Json.JsonSerializer.Serialize(@event), DateTimeOffset.UtcNow);

        await projection.ProjectAsync(envelope);

        Assert.Equal(2200m, (await _index.GetTrendingAsync(10)).Items.Single(i => i.Id == 7).FromPrice);
    }

    [Fact]
    public async Task SearchAmenitiesProjection_overwrites_the_doc_amenities_and_is_filterable()
    {
        await _index.IndexAsync(Doc(11, "Amenity Stay", "Lisbon", country: "PT")); // indexed with no amenities
        var projection = new SearchAmenitiesProjection(_index);
        var @event = new Stay.Catalog.Contracts.PropertyAmenitiesUpdated(
            Guid.NewGuid(), PropertyId: 11, new[] { "WIFI", "POOL" }, DateTimeOffset.UtcNow);
        var envelope = new Stay.BuildingBlocks.Outbox.OutboxEnvelope(
            @event.EventId, @event.EventType, System.Text.Json.JsonSerializer.Serialize(@event), DateTimeOffset.UtcNow);

        await projection.ProjectAsync(envelope);

        var hit = await _index.SearchAsync(new PropertySearchQuery(
            null, null, null, Page: 0, PageSize: 20, Amenities: new[] { "WIFI" }));
        Assert.Equal(1, hit.Total);
        Assert.Contains("WIFI", hit.Items[0].Amenities!);
        Assert.Contains("POOL", hit.Items[0].Amenities!);

        // The AND-semantics amenity filter excludes a property missing one of the requested amenities.
        var miss = await _index.SearchAsync(new PropertySearchQuery(
            null, null, null, Page: 0, PageSize: 20, Amenities: new[] { "SPA" }));
        Assert.Equal(0, miss.Total);
    }

    [Fact]
    public async Task UpdateRating_sets_the_doc_rating_and_review_count()
    {
        await _index.IndexAsync(Doc(1, "Unrated", "Oslo", country: "NO")); // no rating yet

        await _index.UpdateRatingAsync(1, 4.6, 3);

        var doc = (await _index.GetTrendingAsync(10)).Items.Single(i => i.Id == 1);
        Assert.Equal(4.6, doc.Rating);
        Assert.Equal(3, doc.ReviewCount);
    }

    [Fact]
    public async Task RatingProjection_refreshes_the_doc_on_a_published_review()
    {
        await _index.IndexAsync(Doc(7, "Reviewed", "Helsinki", country: "FI"));
        var projection = new RatingProjection(_index);
        var @event = new Stay.Reviews.Contracts.ReviewPublished(
            Guid.NewGuid(), ReviewId: 100, PropertyId: 7, "mod|1", DateTimeOffset.UtcNow, ReviewCount: 2, AvgOverall: 4.0m);
        var envelope = new Stay.BuildingBlocks.Outbox.OutboxEnvelope(
            @event.EventId, @event.EventType, System.Text.Json.JsonSerializer.Serialize(@event), DateTimeOffset.UtcNow);

        await projection.ProjectAsync(envelope);

        var doc = (await _index.GetTrendingAsync(10)).Items.Single(i => i.Id == 7);
        Assert.Equal(4.0, doc.Rating);
        Assert.Equal(2, doc.ReviewCount);
    }

    [Fact]
    public async Task Suggest_returns_live_prefix_matches()
    {
        await _index.IndexAsync(Doc(1, "Marina Bay Suites", "Singapore"));
        await _index.IndexAsync(Doc(2, "Marina Sands", "Singapore"));
        await _index.IndexAsync(Doc(3, "Downtown Loft", "Tokyo", country: "JP"));
        await _index.IndexAsync(Doc(4, "Marina Draft", "Singapore", status: "DRAFT"));

        var suggestions = await _index.SuggestAsync("mari", 10);

        var ids = suggestions.Select(s => s.Id).OrderBy(i => i).ToArray();
        Assert.Equal(new long[] { 1, 2 }, ids); // LIVE marina* only; DRAFT + non-matching excluded
    }

    [Fact]
    public async Task Similar_returns_same_city_properties_excluding_the_seed()
    {
        await _index.IndexAsync(Doc(1, "Seed Hotel", "Vienna", country: "AT", type: "HOTEL", popularity: 1));
        await _index.IndexAsync(Doc(2, "Quiet Neighbour", "Vienna", country: "AT", type: "HOTEL", popularity: 3));
        await _index.IndexAsync(Doc(3, "Popular Neighbour", "Vienna", country: "AT", type: "HOTEL", popularity: 7));
        await _index.IndexAsync(Doc(4, "Faraway", "Berlin", country: "DE", type: "HOTEL", popularity: 99));

        var similar = await _index.GetSimilarAsync(1, 10);

        // Same city only, seed excluded, by popularity desc.
        Assert.Equal(new long[] { 3, 2 }, similar.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task Similar_for_an_unindexed_seed_is_empty()
    {
        var similar = await _index.GetSimilarAsync(404, 10);

        Assert.Empty(similar.Items);
    }

    [Fact]
    public async Task Trending_destinations_rank_cities_by_booking_momentum()
    {
        await _index.IndexAsync(Doc(1, "A", "Paris", country: "FR", popularity: 3));
        await _index.IndexAsync(Doc(2, "B", "Paris", country: "FR", popularity: 5)); // Paris total = 8
        await _index.IndexAsync(Doc(3, "C", "Nice", country: "FR", popularity: 4)); // Nice total = 4
        await _index.IndexAsync(Doc(4, "D", "Nice", country: "FR", status: "DRAFT", popularity: 99)); // excluded

        var destinations = await _index.GetTrendingDestinationsAsync(10);

        Assert.Equal(new[] { "Paris", "Nice" }, destinations.Select(d => d.City).ToArray());
        var paris = destinations[0];
        Assert.Equal("FR", paris.CountryCode);
        Assert.Equal(2, paris.PropertyCount); // DRAFT not counted
        Assert.Equal(8, paris.Popularity);
    }

    [Fact]
    public async Task Restricts_results_to_a_geo_bounding_box()
    {
        // Inside the box (around Bengaluru ~12.97, 77.59) and far outside (Delhi ~28.6, 77.2).
        await _index.IndexAsync(Doc(1, "In Box", "Bengaluru", country: "IN", lat: 12.97, lng: 77.59));
        await _index.IndexAsync(Doc(2, "Out Of Box", "Delhi", country: "IN", lat: 28.61, lng: 77.20));

        var results = await _index.SearchAsync(new PropertySearchQuery(
            null, null, null, 0, 20,
            MinLat: 12.8, MaxLat: 13.1, MinLng: 77.4, MaxLng: 77.8));

        Assert.Equal(1, results.Total);
        Assert.Equal("In Box", results.Items[0].Name);
    }
}
