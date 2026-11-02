namespace SuperMOOO.Data.Sqlite;

internal static class DapperSetup
{
    private static readonly object Gate = new();
    private static bool _done;

    public static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static void Register()
    {
        lock (Gate)
        {
            if (_done) return;
            DefaultTypeMap.MatchNamesWithUnderscores = true;
            SqlMapper.AddTypeHandler(new UnixMsHandler());
            _done = true;
        }
    }

    private sealed class UnixMsHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override void SetValue(System.Data.IDbDataParameter parameter, DateTimeOffset value)
        {
            parameter.DbType = System.Data.DbType.Int64;
            parameter.Value = value.ToUnixTimeMilliseconds();
        }

        public override DateTimeOffset Parse(object value) => DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(value));
    }
}
