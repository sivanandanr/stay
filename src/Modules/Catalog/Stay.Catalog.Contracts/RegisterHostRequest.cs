namespace Stay.Catalog.Contracts;

/// <summary>Body for <c>POST /api/v1/hosts/register</c>. The owner identity comes from the token.</summary>
public sealed record RegisterHostRequest(string DisplayName);
