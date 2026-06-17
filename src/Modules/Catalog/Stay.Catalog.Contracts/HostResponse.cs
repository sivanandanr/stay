namespace Stay.Catalog.Contracts;

/// <summary>The caller's host profile (e.g. for the portal to poll approval status).</summary>
public sealed record HostResponse(long Id, string DisplayName, string Status);
