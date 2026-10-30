using System.Reflection;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using THOBOTTO.Events;
using THOBOTTO.Overview;
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

    // A widget from before layouts holds its block, options and all, and is posted anew.
    [Fact]
    public async Task Widgets_take_their_block_into_a_layout()
    {
        await using var db = database.CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261029134503_GameColours");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO overview_widgets (guild_id, channel_id, "order", kind, options, message_id, shown, created_at)
            VALUES (900101, 900111, 0, 'leaderboard', '{{"count":"5"}}', 900121, 'abc', now());
            """);
        await migrator.MigrateAsync();

        var widget = await db.OverviewWidgets.AsNoTracking().SingleAsync(w => w.GuildId == 900101);
        var cell = Assert.Single(Assert.Single(WidgetLayout.Parse(widget.Layout)));
        Assert.Equal("leaderboard", cell.Kind);
        Assert.Equal("5", cell.Options["count"]);
        Assert.Equal(WidgetStyles.CardsLines, widget.Style);
        Assert.Null(widget.Shown);
        Assert.Equal(900121UL, widget.MessageId);
    }

    // A board becomes a servers widget after its channel's widgets, with the overview channels on where servers were.
    [Fact]
    public async Task A_server_board_becomes_a_widget()
    {
        await using var db = database.CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261029172336_WidgetLayouts");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO server_settings (guild_id, board_channel_id, members_can_add, max_per_member, max_servers, poll_seconds, failures_before_offline)
            VALUES (900201, 900211, true, 5, 25, 60, 2), (900202, NULL, true, 5, 25, 60, 2);
            INSERT INTO enabled_modules (guild_id, module) VALUES (900201, 'game-servers'), (900202, 'game-servers');
            INSERT INTO overview_widgets (guild_id, channel_id, "order", style, layout, created_at)
            VALUES (900201, 900211, 0, 'cards-lines', '[]', now());
            """);
        await migrator.MigrateAsync();

        var widgets = await db.OverviewWidgets.AsNoTracking().Where(w => w.GuildId == 900201 || w.GuildId == 900202).OrderBy(w => w.Order).ToListAsync();
        Assert.Equal(2, widgets.Count);
        var board = widgets[1];
        Assert.Equal((900211UL, 1, WidgetStyles.ColumnsRows), (board.ChannelId, board.Order, board.Style));
        var cell = Assert.Single(Assert.Single(WidgetLayout.Parse(board.Layout)));
        Assert.Equal(("game-servers", "cards", "2"), (cell.Kind, cell.Options["show"], cell.Options["columns"]));
        Assert.True(await db.EnabledModules.AnyAsync(m => m.GuildId == 900201 && m.Module == "overview"));
        Assert.False(await db.EnabledModules.AnyAsync(m => m.GuildId == 900202 && m.Module == "overview"));
    }

    // A picker becomes a widget of the Game roles block's buttons, after its channel's widgets, in order.
    [Fact]
    public async Task A_game_picker_becomes_a_widget()
    {
        await using var db = database.CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261029175137_ServerBoardRetired");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO game_pickers (message_id, guild_id, channel_id) VALUES (900321, 900301, 900311), (900322, 900301, 900311);
            INSERT INTO enabled_modules (guild_id, module) VALUES (900301, 'games');
            INSERT INTO overview_widgets (guild_id, channel_id, "order", style, layout, created_at)
            VALUES (900301, 900311, 0, 'cards-lines', '[]', now());
            """);
        await migrator.MigrateAsync();

        var widgets = await db.OverviewWidgets.AsNoTracking().Where(w => w.GuildId == 900301).OrderBy(w => w.Order).ToListAsync();
        Assert.Equal([0, 1, 2], widgets.Select(w => w.Order));
        var cell = Assert.Single(Assert.Single(WidgetLayout.Parse(widgets[2].Layout)));
        Assert.Equal(("game-roles", "buttons"), (cell.Kind, cell.Options["show"]));
        Assert.True(await db.EnabledModules.AnyAsync(m => m.GuildId == 900301 && m.Module == "overview"));
    }

    // A server that took in Discord's calendar events into a channel goes on taking them in (to the forum now); one
    // without leaves them alone, and the proposals' voting channel goes.
    [Fact]
    public async Task Calendar_imports_stay_on_where_a_channel_was_set()
    {
        await using var db = database.CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261030074932_Happenings");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO module_settings (guild_id, module, json) VALUES
                (900401, 'events', '{{"importChannelId": "900411", "endAfterHours": 3}}'),
                (900402, 'events', '{{"importChannelId": null}}'),
                (900401, 'emojis', '{{"voteChannelId": "900412", "voteDays": 2}}');
            """);
        await migrator.MigrateAsync();

        var rows = await db.ModuleSettings.AsNoTracking().Where(s => s.GuildId == 900401 || s.GuildId == 900402).ToListAsync();
        string Json(ulong guild, string module) => rows.Single(r => r.GuildId == guild && r.Module == module).Json;
        Assert.True(JsonSerializer.Deserialize<EventRules>(Json(900401, "events"), JsonSerializerOptions.Web)!.ImportDiscordEvents);
        Assert.Equal(3, JsonSerializer.Deserialize<EventRules>(Json(900401, "events"), JsonSerializerOptions.Web)!.EndAfterHours);
        Assert.False(JsonSerializer.Deserialize<EventRules>(Json(900402, "events"), JsonSerializerOptions.Web)!.ImportDiscordEvents);
        Assert.DoesNotContain("voteChannelId", Json(900401, "emojis"));
        Assert.DoesNotContain("importChannelId", Json(900401, "events"));
    }
}
