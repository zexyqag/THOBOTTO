using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace THOBOTTO.Backups;

// Channel and role ids inside settings, found by name: every module calls them …ChannelId(s), …CategoryId(s)
// or …RoleId(s), so settings added later are covered too.
public static partial class BackupIds
{
    public static (HashSet<ulong> Channels, HashSet<ulong> Roles) Find(JsonObject settings)
    {
        var (channels, roles) = (new HashSet<ulong>(), new HashSet<ulong>());
        Walk(settings, (kind, id) => (kind == Kind.Role ? roles : channels).Add(id));
        return (channels, roles);
    }

    // A copy with each id swapped for its counterpart; one with none becomes null, or drops out of a list.
    public static JsonObject Remap(JsonObject settings, Func<ulong, ulong?> channel, Func<ulong, ulong?> role)
    {
        var copy = settings.DeepClone().AsObject();
        foreach (var (name, value) in copy.ToList())
        {
            if (KindOf(name) is not { } kind)
            {
                if (value is JsonObject inner)
                    copy[name] = Remap(inner, channel, role);
                continue;
            }
            var map = kind == Kind.Role ? role : channel;
            copy[name] = value switch
            {
                JsonArray list => new JsonArray(list.Select(Id).OfType<ulong>().Select(map).OfType<ulong>().Select(id => (JsonNode)JsonValue.Create(id)).ToArray()),
                not null when Id(value) is { } id && map(id) is { } mapped => JsonValue.Create(mapped),
                _ => null,
            };
        }
        return copy;
    }

    private enum Kind
    {
        Channel,
        Role,
    }

    private static void Walk(JsonObject settings, Action<Kind, ulong> found)
    {
        foreach (var (name, value) in settings)
        {
            if (KindOf(name) is { } kind)
            {
                foreach (var id in value is JsonArray list ? list.Select(Id) : [Id(value)])
                {
                    if (id is { } found1)
                        found(kind, found1);
                }
            }
            else if (value is JsonObject inner)
                Walk(inner, found);
        }
    }

    private static Kind? KindOf(string property) => IdField().Match(property) is { Success: true } m
        ? m.Groups[1].Value.Equals("role", StringComparison.OrdinalIgnoreCase) ? Kind.Role : Kind.Channel
        : null;

    private static ulong? Id(JsonNode? node) => node is JsonValue v && (v.TryGetValue<ulong>(out var n) || v.TryGetValue<string>(out var s) && ulong.TryParse(s, out n)) ? n : null;

    [GeneratedRegex("(channel|category|role)ids?$", RegexOptions.IgnoreCase)]
    private static partial Regex IdField();
}
