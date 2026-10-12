using Microsoft.EntityFrameworkCore;

namespace THOBOTTO.DatabaseTests;

[Collection(DatabaseCollection.Name)]
public class MigrationTests(TestDatabase database)
{
    [Fact]
    public async Task Every_migration_applies()
    {
        await using var db = database.CreateDbContext();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    // A change to the entities without its migration would leave the database behind the code.
    [Fact]
    public void The_migrations_match_the_entities()
    {
        using var db = database.CreateDbContext();
        Assert.False(db.Database.HasPendingModelChanges(), "The entities changed without a migration: add one with dotnet ef migrations add.");
    }

    // Every table can be read through its entity.
    [Fact]
    public async Task Every_table_reads()
    {
        await using var db = database.CreateDbContext();
        var read = typeof(MigrationTests).GetMethod(nameof(ReadAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        foreach (var entity in db.Model.GetEntityTypes().Where(e => !e.IsOwned() && e.GetTableName() is not null && !e.HasSharedClrType))
            await (Task)read.MakeGenericMethod(entity.ClrType).Invoke(null, [db])!;
    }

    private static Task ReadAsync<T>(DbContext db) where T : class => db.Set<T>().Take(1).ToListAsync();
}
