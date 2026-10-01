using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ClubOS.EdgeController.Storage;

internal static class SqlExtensions
{
    public static SqliteCommand Cmd(this SqliteConnection c, SqliteTransaction? tx, string sql,
        params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value switch
            {
                null => DBNull.Value,
                DateTimeOffset d => Iso(d),
                bool b => b ? 1 : 0,
                Enum e => e.ToString(),
                _ => value
            });
        }

        return cmd;
    }

    public static int Exec(this SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = c.Cmd(tx, sql, args);
        return cmd.ExecuteNonQuery();
    }

    public static object? Scalar(this SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = c.Cmd(tx, sql, args);
        return cmd.ExecuteScalar();
    }

    public static List<T> Query<T>(this SqliteConnection c, SqliteTransaction? tx, string sql, Func<SqliteDataReader, T> map,
        params (string, object?)[] args)
    {
        using var cmd = c.Cmd(tx, sql, args);
        using var reader = cmd.ExecuteReader();
        var list = new List<T>();
        while (reader.Read())
        {
            list.Add(map(reader));
        }

        return list;
    }

    /// <summary>Время хранится как ISO-8601 UTC (round-trip, лексикографически сортируется).</summary>
    public static string Iso(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseIso(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public static string? Str(this SqliteDataReader r, string col) =>
        r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetString(r.GetOrdinal(col));

    public static string S(this SqliteDataReader r, string col) => r.GetString(r.GetOrdinal(col));

    public static long L(this SqliteDataReader r, string col) => r.GetInt64(r.GetOrdinal(col));

    public static long? LN(this SqliteDataReader r, string col) =>
        r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetInt64(r.GetOrdinal(col));

    public static DateTimeOffset T(this SqliteDataReader r, string col) => ParseIso(r.S(col));

    public static DateTimeOffset? TN(this SqliteDataReader r, string col) => r.Str(col) is { } s ? ParseIso(s) : null;

    public static bool IsUniqueViolation(this SqliteException ex) =>
        ex.SqliteErrorCode == 19 /* SQLITE_CONSTRAINT */;
}
