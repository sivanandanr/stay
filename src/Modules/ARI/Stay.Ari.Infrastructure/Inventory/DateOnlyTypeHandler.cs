using System.Data;
using Dapper;

namespace Stay.Ari.Infrastructure.Inventory;

/// <summary>
/// Teaches Dapper to bind <see cref="DateOnly"/> parameters (it doesn't natively in 2.1.x). Npgsql
/// maps <see cref="DateOnly"/> to the PostgreSQL <c>date</c> type.
/// </summary>
public sealed class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
{
    public override void SetValue(IDbDataParameter parameter, DateOnly value) => parameter.Value = value;

    public override DateOnly Parse(object value) => value switch
    {
        DateOnly d => d,
        DateTime dt => DateOnly.FromDateTime(dt),
        _ => DateOnly.FromDateTime(Convert.ToDateTime(value))
    };
}
