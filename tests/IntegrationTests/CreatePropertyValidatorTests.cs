using Stay.Catalog.Application.Properties.CreateProperty;
using Stay.Catalog.Contracts;

namespace Stay.IntegrationTests;

/// <summary>Pure validator checks — no database needed.</summary>
public sealed class CreatePropertyValidatorTests
{
    private static CreatePropertyCommand Command(
        string name = "Valid Name",
        string propertyType = "HOTEL",
        string currency = "SGD",
        string countryCode = "SG",
        double latitude = 1.0) => new(
        OwnerSub: "auth0|abc",
        Name: name,
        PropertyType: propertyType,
        Description: null,
        StarRating: null,
        Latitude: latitude,
        Longitude: 103.0,
        CountryCode: countryCode,
        CityId: 1,
        Address: new AddressDto("Line 1", null, "City", null, "00000", "SG"),
        DefaultCurrency: currency,
        Timezone: "Asia/Singapore",
        CheckInTime: null,
        CheckOutTime: null);

    private readonly CreatePropertyValidator _validator = new();

    [Fact]
    public void Accepts_a_well_formed_command() =>
        Assert.True(_validator.Validate(Command()).IsValid);

    [Theory]
    [InlineData("")]                 // empty name
    [InlineData(null)]
    public void Rejects_missing_name(string? name) =>
        Assert.False(_validator.Validate(Command(name: name!)).IsValid);

    [Fact]
    public void Rejects_unknown_property_type() =>
        Assert.False(_validator.Validate(Command(propertyType: "CASTLE")).IsValid);

    [Fact]
    public void Rejects_bad_currency_length() =>
        Assert.False(_validator.Validate(Command(currency: "RUPEE")).IsValid);

    [Fact]
    public void Rejects_out_of_range_latitude() =>
        Assert.False(_validator.Validate(Command(latitude: 95.0)).IsValid);
}
