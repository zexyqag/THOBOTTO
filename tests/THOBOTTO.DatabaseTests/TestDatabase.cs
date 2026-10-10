using Microsoft.EntityFrameworkCore;

using Npgsql;

using THOBOTTO.Data;

namespace THOBOTTO.DatabaseTests;

// A database of its own for the run, on the Postgres server in TEST_POSTGRES (CI starts one), migrated as the bot
// does at startup and dropped after. Each test uses its own server ids, so they don't see each other's rows.
public sealed class TestDatabase : IDbContextFactory<BotDbContext>, IAsyncLifetime
{
    private readonly string _server = Environment.GetEnvironmentVariable("TEST_POSTGRES")
        ?? throw new InvalidOperationException("Set TEST_POSTGRES to a Postgres server these tests may create databases on, e.g. Host=localhost;Username=postgres;Password=postgres");
    private readonly string _name = $"thobotto_test_{Guid.NewGuid():N}";

    private DbContextOptions<BotDbContext> _options = null!;

    public BotDbContext CreateDbContext() => new(_options);

    public async Task InitializeAsync()
    {
        await RunAsync($"CREATE DATABASE {_name}");
        _options = new DbContextOptionsBuilder<BotDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(_server) { Database = _name }.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await RunAsync($"DROP DATABASE IF EXISTS {_name} WITH (FORCE)");
    }

    private async Task RunAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_server);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<TestDatabase>
{
    public const string Name = "database";
}
