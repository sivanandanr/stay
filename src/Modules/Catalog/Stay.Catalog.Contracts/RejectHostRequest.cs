namespace Stay.Catalog.Contracts;

/// <summary>Body for <c>POST /api/v1/admin/hosts/{id}/reject</c>. A reason is mandatory (CLAUDE.md §10).</summary>
public sealed record RejectHostRequest(string Reason);
