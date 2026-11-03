using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Archive;
using THOBOTTO.Data;
using THOBOTTO.Discord;
using THOBOTTO.Members;
using THOBOTTO.Panel;

namespace THOBOTTO.Kaomoji;

public static class KaomojiStartup
{
    public static IServiceCollection AddKaomoji(this IServiceCollection services)
        => services
            .AddSingleton<KaomojiShelf>()
            .AddSingleton<IExportSource, KaomojiExport>()
            .AddSingleton<IMemberData, KaomojiMemberData>()
            .AddSingleton(new GuildNavEntry("kaomoji", "Kaomoji", GuildNavGroup.Community, 45, Module: KaomojiShelf.ModuleId));
}

// The server's own faces, for the archive's export.
public sealed class KaomojiExport(IDbContextFactory<BotDbContext> dbFactory) : IExportSource
{
    public string Name => "kaomoji";

    public async Task WriteAsync(ulong guildId, Utf8JsonWriter json)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        json.WriteStartArray();
        await foreach (var kaomoji in db.ServerKaomojis.AsNoTracking().Where(k => k.GuildId == guildId).OrderBy(k => k.Id).AsAsyncEnumerable())
            KaomojiMemberData.Write(json, kaomoji);
        json.WriteEndArray();
    }
}

// The faces a member added to the server.
public sealed class KaomojiMemberData(IDbContextFactory<BotDbContext> dbFactory) : IMemberData
{
    public string Name => "Kaomoji";

    public async Task<long> CountAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ServerKaomojis.LongCountAsync(k => k.GuildId == guildId && k.AddedById == userId);
    }

    public async Task WriteAsync(ulong guildId, ulong userId, Utf8JsonWriter json)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        json.WriteStartArray();
        await foreach (var kaomoji in db.ServerKaomojis.AsNoTracking().Where(k => k.GuildId == guildId && k.AddedById == userId).OrderBy(k => k.Id).AsAsyncEnumerable())
            Write(json, kaomoji);
        json.WriteEndArray();
    }

    public static void Write(Utf8JsonWriter json, ServerKaomoji kaomoji)
    {
        json.WriteStartObject();
        json.WriteString("text", kaomoji.Text);
        json.WriteString("name", kaomoji.Name);
        json.WriteString("tags", kaomoji.Tags);
        json.WriteId("addedBy", kaomoji.AddedById);
        json.WriteString("addedAt", kaomoji.AddedAt);
        json.WriteEndObject();
    }
}
