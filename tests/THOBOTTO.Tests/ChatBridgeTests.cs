using System.Text.Json;

using THOBOTTO.GameServers;

namespace THOBOTTO.Tests;

public class ChatBridgeTests
{
    [Fact]
    public void Seven_days_to_die_chat_is_a_players_global_line()
    {
        Assert.Equal(("Sprig", "anyone up for a horde night?"),
            ChatBridge.SevenDaysChat("2026-10-31T20:00:00 1234.567 INF Chat (from 'Steam_76561198000000001', entity id '171', to 'Global'): 'Sprig': anyone up for a horde night?"));
        // The bot's own say comes back as the server's.
        Assert.Null(ChatBridge.SevenDaysChat("2026-10-31T20:00:01 1234.600 INF Chat (from '-non-player-', entity id '-1', to 'Global'): 'Server': [Discord] Ana: hi"));
        Assert.Null(ChatBridge.SevenDaysChat("2026-10-31T20:00:02 1234.700 INF Chat (from 'Steam_76561198000000001', entity id '171', to 'Party'): 'Sprig': secret plan"));
        Assert.Null(ChatBridge.SevenDaysChat("2026-10-31T20:00:03 1234.800 INF Player connected, entity id '171'"));
    }

    [Fact]
    public void Rust_chat_is_a_players_global_message()
    {
        JsonElement Sent(object chat, string type = "Chat") => JsonSerializer.SerializeToElement(new { Message = JsonSerializer.Serialize(chat), Identifier = 0, Type = type });
        Assert.Equal(("Steve", "raid at dawn"), ChatBridge.RustChat(Sent(new { Channel = 0, Message = "raid at dawn", UserId = "76561198000000002", Username = "Steve" })));
        Assert.Null(ChatBridge.RustChat(Sent(new { Channel = 0, Message = "[Discord] Ana: hi", UserId = "0", Username = "SERVER" })));
        Assert.Null(ChatBridge.RustChat(Sent(new { Channel = 1, Message = "team only", UserId = "76561198000000002", Username = "Steve" })));
        Assert.Null(ChatBridge.RustChat(Sent(new { Channel = 0, Message = "x", UserId = "1", Username = "x" }, type: "Generic")));
    }

    [Fact]
    public void The_bridges_own_lines_arent_passed_on_again()
    {
        string[] servers = ["Survival", "Navezgane"];
        Assert.True(ChatBridge.Ours("[Discord] Ana: hi", servers));
        Assert.True(ChatBridge.Ours("[Navezgane] Sprig: hi", servers));
        Assert.False(ChatBridge.Ours("hi [Discord]", servers));
        Assert.False(ChatBridge.Ours("[Other] Bob: hi", servers));
    }

    [Fact]
    public void A_webhooks_player_is_its_name_without_the_server()
    {
        Assert.Equal("Sprig", ChatBridge.Player("Sprig · Navezgane", "Navezgane"));
        Assert.Equal("Steve", ChatBridge.Player("Steve", "Survival"));
    }

    [Fact]
    public void Names_discord_refuses_for_webhooks_are_broken_up()
    {
        Assert.DoesNotContain("discord", ChatBridge.Speakable("DiscordFan · Survival"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(80, ChatBridge.Speakable(new string('a', 100)).Length);
    }
}
