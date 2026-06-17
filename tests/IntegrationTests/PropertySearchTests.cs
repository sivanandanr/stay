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
        long id, string name, string city, string country = "SG", string status = "LIVE", decimal? price = 100m) =>
        new(id, name, city, country, "HOTEL", 1.29, 103.85, price, status);

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
}
