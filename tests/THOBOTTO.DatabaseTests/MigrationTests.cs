using System.Reflection;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using THOBOTTO.Voice.Channels;

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
        var read = typeof(MigrationTests).GetMethod(nameof(ReadAsync), BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var entity in db.Model.GetEntityTypes().Where(e => !e.IsOwned() && e.GetTableName() is not null && !e.HasSharedClrType))
            await (Task)read.MakeGenericMethod(entity.ClrType).Invoke(null, [db])!;
    }

    private static Task ReadAsync<T>(DbContext db) where T : class => db.Set<T>().Take(1).ToListAsync();

    // The hubs table's rows land in the voice channels settings, merged into a row the server already had.
    [Fact]
    public async Task Voice_hubs_move_into_the_settings()
    {
        await using var db = database.CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261021173406_ArchivedMessageTimeIndex");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO voice_hubs (channel_id, guild_id) VALUES (900011, 900001), (900012, 900001), (900021, 900002);
            INSERT INTO module_settings (guild_id, module, json) VALUES (900001, 'voice-channels', '{{"maxChannels": 3}}');
            """);
        await migrator.MigrateAsync();

        var rows = await db.ModuleSettings.AsNoTracking().Where(s => s.Module == "voice-channels" && s.GuildId >= 900001 && s.GuildId <= 900002).OrderBy(s => s.GuildId).ToListAsync();
        var rules = rows.Select(r => JsonSerializer.Deserialize<VoiceRules>(r.Json, JsonSerializerOptions.Web)!).ToList();
        Assert.Equal(2, rules.Count);
        Assert.Equal(3, rules[0].MaxChannels);
        Assert.Equal([900011UL, 900012UL], rules[0].HubChannelIds.Order());
        Assert.Equal([900021UL], rules[1].HubChannelIds);
    }
}
