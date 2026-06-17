namespace Stay.BuildingBlocks.Outbox;

/// <summary>
/// Guards the per-context schema name that gets interpolated into outbox SQL. The value comes
/// from trusted configuration, not user input, but we whitelist it anyway (defence in depth):
/// a schema identifier can only be letters, digits and underscores.
/// </summary>
internal static class SchemaName
{
    public static void Validate(string schema)
    {
        if (string.IsNullOrWhiteSpace(schema) || !schema.All(c => char.IsLetterOrDigit(c) || c == '_'))
            throw new ArgumentException($"Invalid outbox schema name '{schema}'.", nameof(schema));
    }
}
