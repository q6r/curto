using Microsoft.Data.Sqlite;

namespace Curto.Api.Storage;

/// <summary>Small helpers that keep the repositories free of ADO.NET ceremony. All SQL stays parameterised.</summary>
internal static class SqliteExtensions
{
    /// <summary>Creates a command bound to <paramref name="connection"/> (and <paramref name="transaction"/> when given).</summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="sql">Constant SQL text; values are passed only through parameters.</param>
    /// <param name="transaction">Transaction the command belongs to.</param>
    public static SqliteCommand CreateCommand(this SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    /// <summary>Adds a named parameter, mapping null to SQL NULL.</summary>
    /// <param name="command">Target command.</param>
    /// <param name="name">Parameter name including the <c>$</c> prefix.</param>
    /// <param name="value">Parameter value.</param>
    public static SqliteCommand With(this SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    /// <summary>Reads a nullable text column.</summary>
    /// <param name="reader">Reader positioned on a row.</param>
    /// <param name="ordinal">Column index.</param>
    public static string? GetNullableString(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>Reads a nullable integer column.</summary>
    /// <param name="reader">Reader positioned on a row.</param>
    /// <param name="ordinal">Column index.</param>
    public static long? GetNullableInt64(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
}

/// <summary>Unix-second conversions used by every timestamp column.</summary>
internal static class UnixTime
{
    /// <summary>Converts an instant to whole seconds since the Unix epoch.</summary>
    /// <param name="value">Instant.</param>
    public static long ToDb(DateTimeOffset value) => value.ToUnixTimeSeconds();

    /// <summary>Converts a nullable instant.</summary>
    /// <param name="value">Instant or null.</param>
    public static long? ToDb(DateTimeOffset? value) => value?.ToUnixTimeSeconds();

    /// <summary>Converts stored seconds back to a UTC instant.</summary>
    /// <param name="seconds">Seconds since the Unix epoch.</param>
    public static DateTimeOffset FromDb(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds);

    /// <summary>Converts a stored nullable value.</summary>
    /// <param name="seconds">Seconds since the Unix epoch, or null.</param>
    public static DateTimeOffset? FromDb(long? seconds) => seconds is { } s ? FromDb(s) : null;
}
