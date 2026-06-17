using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Stay.BuildingBlocks.Messaging;
using Stay.BuildingBlocks.Outbox;
using Stay.Catalog.Contracts;
using Stay.Search.Infrastructure;

namespace Stay.IntegrationTests;

/// <summary>
/// The catalog → search pipeline: catalog events drive the OpenSearch read model. A property becomes
/// searchable only once it's published, and disappears again if rejected.
/// </summary>
public sealed class SearchIndexProjectionTests : IAsyncLifetime
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
    private SearchIndexProjection _projection = null!;

    public async Task InitializeAsync()
    {
        await _opensearch.StartAsync();
        var http = new HttpClient
        {
            BaseAddress = new Uri($"http://{_opensearch.Hostname}:{_opensearch.GetMappedPublicPort(9200)}")
        };
        _index = new OpenSearchPropertyIndex(http);
        await _index.EnsureIndexAsync();
        _projection = new SearchIndexProjection(_index);
    }

    public Task DisposeAsync() => _opensearch.DisposeAsync().AsTask();

    private static OutboxEnvelope EnvelopeFor(IIntegrationEvent @event) =>
        new(@event.EventId, @event.EventType, JsonSerializer.Serialize(@event, @event.GetType()), DateTimeOffset.UtcNow);

    private Task Project(IIntegrationEvent @event) => _projection.ProjectAsync(EnvelopeFor(@event));

    private Task<SearchResults> SearchCity(string city) =>
        _index.SearchAsync(new PropertySearchQuery(city, null, null, 0, 20));

    [Fact]
    public async Task Created_property_is_indexed_but_not_searchable_until_published()
    {
        await Project(new PropertyCreated(
            Guid.NewGuid(), PropertyId: 1, HostId: 9, "Marina Bay Suites", "HOTEL",
            "SG", CityId: 5, "Singapore", 1.2834, 103.8607, DateTimeOffset.UtcNow));

        // DRAFT → excluded from search.
        Assert.Equal(0, (await SearchCity("Singapore")).Total);

        // Publish → searchable, with the event-carried fields.
        await Project(new PropertyPublished(Guid.NewGuid(), PropertyId: 1, "admin|1", DateTimeOffset.UtcNow));

        var results = await SearchCity("Singapore");
        Assert.Equal(1, results.Total);
        Assert.Equal("Marina Bay Suites", results.Items[0].Name);
        Assert.Equal("HOTEL", results.Items[0].PropertyType);
        Assert.Equal("LIVE", results.Items[0].Status);
    }

    [Fact]
    public async Task Rejecting_a_published_property_removes_it_from_search()
    {
        await Project(new PropertyCreated(
            Guid.NewGuid(), 2, 9, "City Hotel", "HOTEL", "TH", 6, "Bangkok", 13.75, 100.5, DateTimeOffset.UtcNow));
        await Project(new PropertyPublished(Guid.NewGuid(), 2, "admin|1", DateTimeOffset.UtcNow));
        Assert.Equal(1, (await SearchCity("Bangkok")).Total);

        await Project(new PropertyRejected(Guid.NewGuid(), 2, "admin|1", "Policy issue.", DateTimeOffset.UtcNow));

        Assert.Equal(0, (await SearchCity("Bangkok")).Total); // back to DRAFT → hidden
    }

    [Fact]
    public async Task Publishing_an_unknown_property_is_a_no_op()
    {
        // No PropertyCreated was projected first — the update finds nothing and doesn't throw.
        await Project(new PropertyPublished(Guid.NewGuid(), 999, "admin|1", DateTimeOffset.UtcNow));

        Assert.Equal(0, (await SearchCity("Nowhere")).Total);
    }
}
