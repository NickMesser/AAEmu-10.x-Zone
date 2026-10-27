using Microsoft.Data.Sqlite;

namespace AAEmu.Zone.Content;

// Native generated loaders use sqlite3_column_int/double/text, including NULL defaults.
internal static class NativeContentReader
{
    internal static int Int32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : unchecked((int)reader.GetInt64(ordinal));
    internal static uint UInt32(SqliteDataReader reader, int ordinal) => unchecked((uint)Int32(reader, ordinal));
    internal static float Single(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : (float)reader.GetDouble(ordinal);
    internal static string String(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal);
    internal static bool Boolean(SqliteDataReader reader, int ordinal) =>
        String(reader, ordinal) is [ 'T' or 't' or '1', .. ];

    internal static void Read(SqliteConnection connection, string sql, Action<SqliteDataReader> consume)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read()) consume(reader);
    }
}
