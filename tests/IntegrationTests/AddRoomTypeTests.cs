using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Stay.Catalog.Application.Properties.AddRoomType;
using Stay.Catalog.Application.Properties.CreateProperty;
using Stay.Catalog.Contracts;
using Stay.Catalog.Domain.Properties;
using Stay.Catalog.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>AddRoomType against real Postgres: owner-scoped insert, enum/jsonb mapping, outbox event.</summary>
public sealed class AddRoomTypeTests : IAsyncLifetime
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

    private async Task<string> SeedHostAsync()
    {
        var sub = $"auth0|{Guid.NewGuid():N}";
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync("""
            INSERT INTO catalog.host (identity_sub, display_name, status)
            VALUES (@sub, 'Acme Stays', 'ACTIVE')
            """, new { sub });
        return sub;
    }

    private async Task<long> CreatePropertyAsync(string ownerSub, long cityId)
    {
        await using var db = NewDbContext();
        var command = new CreatePropertyCommand(
            OwnerSub: ownerSub, Name: "Marina Bay Suites", PropertyType: "HOTEL",
            Description: null, StarRating: 5, Latitude: 1.2834, Longitude: 103.8607,
            CountryCode: "SG", CityId: cityId,
            Address: new AddressDto("1 Bayfront Ave", null, "Singapore", null, "018971", "SG"),
            DefaultCurrency: "SGD", Timezone: "Asia/Singapore",
            CheckInTime: null, CheckOutTime: null);
        var result = await new CreatePropertyHandler(db).Handle(command, default);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value;
    }

    private static AddRoomTypeCommand Command(string sub, long propertyId, short baseOcc = 2, short maxOcc = 4) => new(
        OwnerSub: sub,
        PropertyId: propertyId,
        Name: "Deluxe King",
        UnitKind: "ROOM",
        TotalUnits: 10,
        BaseOccupancy: baseOcc,
        MaxOccupancy: maxOcc,
        MaxAdults: 3,
        MaxChildren: 1,
        BedConfig: new BedConfigDto(Doubles: 1, Singles: 0, Sofabeds: 1),
        SizeSqm: 32.5m);

    [Fact]
    public async Task Owner_adds_a_room_type_and_emits_RoomTypeAdded()
    {
        var cityId = await SeedCityAsync();
        var sub = await SeedHostAsync();
        var propertyId = await CreatePropertyAsync(sub, cityId);

        long roomTypeId;
        await using (var db = NewDbContext())
        {
            var result = await new AddRoomTypeHandler(db).Handle(Command(sub, propertyId), default);
            Assert.True(result.IsSuccess, result.Error?.Message);
            roomTypeId = result.Value;
        }

        await using (var db = NewDbContext())
        {
            var room = await db.RoomTypes.SingleAsync(r => r.Id == roomTypeId);
            Assert.Equal(propertyId, room.PropertyId);
            Assert.Equal(UnitKind.Room, room.UnitKind);
            Assert.Equal(10, room.TotalUnits);
            Assert.Equal((short)2, room.BaseOccupancy);
            Assert.Equal((short)4, room.MaxOccupancy);
            Assert.Equal(32.5m, room.SizeSqm);
            Assert.NotNull(room.BedConfig);
            Assert.Equal(1, room.BedConfig!.Doubles);   // jsonb round-trip
            Assert.Equal(1, room.BedConfig!.Sofabeds);

            // One RoomTypeAdded event (plus the PropertyCreated from seeding) — assert ours exists.
            Assert.Equal(1, await db.OutboxMessages.CountAsync(m => m.Type == "stay.catalog.room-type-added"));
        }
    }

    [Fact]
    public async Task Adding_to_another_owners_property_is_rejected_and_nothing_is_written()
    {
        var cityId = await SeedCityAsync();
        var ownerSub = await SeedHostAsync();
        var otherSub = await SeedHostAsync();
        var propertyId = await CreatePropertyAsync(ownerSub, cityId);

        await using var db = NewDbContext();
        var result = await new AddRoomTypeHandler(db).Handle(Command(otherSub, propertyId), default);

        Assert.False(result.IsSuccess);
        Assert.Equal("property-not-found", result.Error!.Value.Code); // tenancy: don't leak existence
        Assert.Equal(0, await db.RoomTypes.CountAsync());
        Assert.Equal(0, await db.OutboxMessages.CountAsync(m => m.Type == "stay.catalog.room-type-added"));
    }

    [Fact]
    public async Task Adding_to_an_unknown_property_is_rejected()
    {
        var sub = await SeedHostAsync();

        await using var db = NewDbContext();
        var result = await new AddRoomTypeHandler(db).Handle(Command(sub, propertyId: 999_999), default);

        Assert.False(result.IsSuccess);
        Assert.Equal("property-not-found", result.Error!.Value.Code);
    }
}
