using NetCord.Rest;

namespace THOBOTTO.Discord;

// What Discord said when it refused a request, with the fields it named (a 400's "Invalid Form Body" alone says
// nothing of which part was wrong).
public static class DiscordErrors
{
    public static string Describe(RestException ex)
    {
        if (ex.Error is not { } error)
            return ex.Message;
        var details = new List<string>();
        Walk(error.Error, "", details);
        return details.Count == 0 ? $"{ex.Message} {error.Message}" : $"{error.Message} {string.Join("; ", details)}";
    }

    private static void Walk(IRestErrorGroup? group, string path, List<string> details)
    {
        switch (group)
        {
            case RestErrorDetailGroup detail:
                details.AddRange(detail.Errors.Select(e => $"{path}: {e.Message}"));
                break;
            case RestErrorGroup nested:
                foreach (var (key, child) in nested.Errors)
                    Walk(child, path.Length == 0 ? key : $"{path}.{key}", details);
                break;
        }
    }
}
