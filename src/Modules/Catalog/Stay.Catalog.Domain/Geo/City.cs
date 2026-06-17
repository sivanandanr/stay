namespace Stay.Catalog.Domain.Geo;

/// <summary>
/// Master-data city. Modelled read-only here so CreateProperty can verify the referenced city
/// exists before inserting (the DB also enforces the FK within the catalog context).
/// </summary>
public sealed class City
{
    private City() { } // EF materialization

    public long Id { get; private set; }
    public string Name { get; private set; } = null!;
}
