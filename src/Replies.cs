using NetCord;
using NetCord.Rest;

namespace THOBOTTO;

public static class Replies
{
    public static InteractionMessageProperties Ephemeral(string content) => new()
    {
        Content = content,
        Flags = MessageFlags.Ephemeral,
    };
}
