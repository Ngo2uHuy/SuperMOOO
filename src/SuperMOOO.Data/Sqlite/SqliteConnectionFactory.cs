using Microsoft.Data.Sqlite;

namespace SuperMOOO.Data.Sqlite;

[Service(As = [typeof(IDbConnectionFactory)])]
public sealed class SqliteConnectionFactory : IDbConnectionFactory
{
    private const string Pragmas =
        "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";

    private readonly string _connectionString;

    static SqliteConnectionFactory() => DapperSetup.Register();

    public SqliteConnectionFactory(AppPaths paths)
    {
        Directory.CreateDirectory(paths.DataRoot);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.Database,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public DbConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragmas;
        cmd.ExecuteNonQuery();
        return connection;
    }
}
