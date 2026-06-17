using Stay.Catalog.Application.Properties.AddRoomType;
using Stay.Catalog.Contracts;

namespace Stay.IntegrationTests;

public sealed class AddRoomTypeValidatorTests
{
    private static AddRoomTypeCommand Command(
        string unitKind = "ROOM",
        short baseOcc = 2,
        short maxOcc = 4,
        int totalUnits = 5) => new(
        OwnerSub: "auth0|abc",
        PropertyId: 1,
        Name: "Deluxe",
        UnitKind: unitKind,
        TotalUnits: totalUnits,
        BaseOccupancy: baseOcc,
        MaxOccupancy: maxOcc,
        MaxAdults: null,
        MaxChildren: null,
        BedConfig: null,
        SizeSqm: null);

    private readonly AddRoomTypeValidator _validator = new();

    [Fact]
    public void Accepts_a_well_formed_command() =>
        Assert.True(_validator.Validate(Command()).IsValid);

    [Fact]
    public void Rejects_max_occupancy_below_base() =>
        Assert.False(_validator.Validate(Command(baseOcc: 4, maxOcc: 2)).IsValid);

    [Fact]
    public void Rejects_unknown_unit_kind() =>
        Assert.False(_validator.Validate(Command(unitKind: "TENT")).IsValid);

    [Fact]
    public void Rejects_negative_total_units() =>
        Assert.False(_validator.Validate(Command(totalUnits: -1)).IsValid);
}
