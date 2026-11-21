using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Penrill.Storage.Sqlite;

public sealed class SqliteStorageOptions
{
    public string DataDirectory { get; set; } = "";
}

public sealed class SqliteDatabase
{
    private readonly string connectionString;

    public SqliteDatabase(IOptions<SqliteStorageOptions> options, ILogger<SqliteDatabase> logger)
    {
        DataDirectory = Path.GetFullPath(options.Value.DataDirectory);
        Directory.CreateDirectory(DataDirectory);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(DataDirectory, "boards.db"),
            Pooling = true
        }.ToString();
        new SqliteMigrator(this, new FileAssetStore(options), logger).Migrate();
    }

    public string DataDirectory { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        connection.Execute("PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL;");
        return connection;
    }
}

internal static class SqliteCommands
{
    public static SqliteCommand Command(this SqliteConnection connection, string sql, params ReadOnlySpan<(string Name, object? Value)> args)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in args) command.Parameters.AddWithValue("$" + name, value ?? DBNull.Value);
        return command;
    }

    public static int Execute(this SqliteConnection connection, string sql, params ReadOnlySpan<(string Name, object? Value)> args)
    {
        using var command = connection.Command(sql, args);
        return command.ExecuteNonQuery();
    }

    public static object? Scalar(this SqliteConnection connection, string sql, params ReadOnlySpan<(string Name, object? Value)> args)
    {
        using var command = connection.Command(sql, args);
        return command.ExecuteScalar();
    }

    public static List<T> Rows<T>(this SqliteConnection connection, string sql, Func<SqliteDataReader, T> map, params ReadOnlySpan<(string Name, object? Value)> args)
    {
        using var command = connection.Command(sql, args);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(map(reader));
        return rows;
    }

    public static string Stamp(DateTimeOffset at) => at.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseStamp(string text) => DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
}

internal static class SqlAccess
{
    public const string BoardsOf = """
        SELECT board FROM owners WHERE user=$user
        UNION SELECT folder_boards.board FROM folder_boards JOIN members ON members.folder=folder_boards.folder WHERE members.user=$user
        UNION SELECT board FROM board_people WHERE user=$user
        """;
}
