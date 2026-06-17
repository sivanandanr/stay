using NetArchTest.Rules;
using Xunit;

public class ModuleBoundaryTests
{
    [Fact]
    public void Catalog_must_not_depend_on_Booking_Infrastructure()
    {
        var result = Types.InAssembly(typeof(Stay.Catalog.Infrastructure.CatalogModule).Assembly)
            .That().ResideInNamespaceStartingWith("Stay.Catalog")
            .ShouldNot().HaveDependencyOn("Stay.Booking.Infrastructure")
            .GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? new List<string>()));
    }
}
