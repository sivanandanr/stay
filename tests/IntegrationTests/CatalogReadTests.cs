using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Stay.Catalog.Application.Hosts.GetMyHost;
using Stay.Catalog.Application.Properties.CreateProperty;
using Stay.Catalog.Application.Properties.GetPropertyById;
using Stay.Catalog.Contracts;
using Stay.Catalog.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>Owner-scoped read queries (GetMyHost, GetPropertyById) against real Postgres.</summary>
public sealed class CatalogReadTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgis/postgis:16-3.4").Build();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(CatalogSchema.Ddl);
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private CatalogDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(_postgres.GetConnectionString(), o => o.UseNetTopologySuite())
            .Options;
        return new CatalogDbContext(options);
    }

    private async Task<long> SeedCityAsync()
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO catalog.city (name, country_code, geo, timezone)
            VALUES ('Singapore', 'SG', ST_SetSRID(ST_MakePoint(103.85, 1.29), 4326)::geography, 'Asia/Singapore')
            RETURNING id
            """);
    }

    private async Task<string> SeedHostAsync(string status = "ACTIVE")
    {
        var sub = $"auth0|{Guid.NewGuid():N}";
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync("""
            INSERT INTO catalog.host (identity_sub, display_name, status)
            VALUES (@sub, 'Acme Stays', @status)
            """, new { sub, status });
        return sub;
    }

    private async Task<long> CreatePropertyAsync(string ownerSub, long cityId)
    {
        await using var db = NewDbContext();
        var command = new CreatePropertyCommand(
            OwnerSub: ownerSub, Name: "Marina Bay Suites", PropertyType: "HOTEL",
            Description: "Central.", StarRating: 5, Latitude: 1.2834, Longitude: 103.8607,
            CountryCode: "SG", CityId: cityId,
            Address: new AddressDto("1 Bayfront Ave", null, "Singapore", null, "018971", "SG"),
            DefaultCurrency: "SGD", Timezone: "Asia/Singapore",
            CheckInTime: new TimeOnly(14, 0), CheckOutTime: new TimeOnly(11, 0));
        var result = await new CreatePropertyHandler(db).Handle(command, default);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value;
    }

    [Fact]
    public async Task GetMyHost_returns_the_callers_host()
    {
        var sub = await SeedHostAsync("PENDING");

        await using var db = NewDbContext();
        var result = await new GetMyHostHandler(db).Handle(new GetMyHostQuery(sub), default);

        Assert.True(result.IsSuccess);
        Assert.Equal("PENDING", result.Value!.Status);
        Assert.Equal("Acme Stays", result.Value!.DisplayName);
    }

    [Fact]
    public async Task GetMyHost_is_not_found_for_an_unregistered_subject()
    {
        await using var db = NewDbContext();
        var result = await new GetMyHostHandler(db).Handle(new GetMyHostQuery("auth0|nobody"), default);

        Assert.False(result.IsSuccess);
        Assert.Equal("host-not-registered", result.Error!.Value.Code);
    }

    [Fact]
    public async Task GetPropertyById_returns_the_owners_property_with_full_detail()
    {
        var cityId = await SeedCityAsync();
        var sub = await SeedHostAsync();
        var propertyId = await CreatePropertyAsync(sub, cityId);

        await using var db = NewDbContext();
        var result = await new GetPropertyByIdHandler(db).Handle(new GetPropertyByIdQuery(sub, propertyId), default);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var property = result.Value!;
        Assert.Equal(propertyId, property.Id);
        Assert.Equal("HOTEL", property.PropertyType);
        Assert.Equal("DRAFT", property.Status);
        Assert.Equal(103.8607, property.Longitude, 4);
        Assert.Equal(1.2834, property.Latitude, 4);
        Assert.Equal("1 Bayfront Ave", property.Address.Line1);
        Assert.Equal(new TimeOnly(14, 0), property.CheckInTime);
    }

    [Fact]
    public async Task GetPropertyById_hides_another_owners_property()
    {
        var cityId = await SeedCityAsync();
        var ownerSub = await SeedHostAsync();
        var otherSub = await SeedHostAsync();
        var propertyId = await CreatePropertyAsync(ownerSub, cityId);

        await using var db = NewDbContext();
        var result = await new GetPropertyByIdHandler(db).Handle(new GetPropertyByIdQuery(otherSub, propertyId), default);

        Assert.False(result.IsSuccess);
        Assert.Equal("property-not-found", result.Error!.Value.Code); // tenancy: don't leak existence
    }

    [Fact]
    public async Task GetPropertyById_is_not_found_for_unknown_id()
    {
        var sub = await SeedHostAsync();

        await using var db = NewDbContext();
        var result = await new GetPropertyByIdHandler(db).Handle(new GetPropertyByIdQuery(sub, 999_999), default);

        Assert.False(result.IsSuccess);
        Assert.Equal("property-not-found", result.Error!.Value.Code);
    }
}
