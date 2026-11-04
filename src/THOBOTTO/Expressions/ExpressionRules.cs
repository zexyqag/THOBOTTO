using THOBOTTO.Modules;

namespace THOBOTTO.Expressions;

public static class EmojiListChoices
{
    public const string MostUsed = "most-used";
    public const string Managers = "managers";
}

// Stored with SettingsStore under the module id. Points parts apply only while the points module is on.
public sealed record ExpressionRules
{
    [Setting("Cost to propose", Help = "Refunded if accepted.", Unit = "points", Min = 0, Max = 1_000_000)]
    public double ProposeCost { get; init; } = 10;

    [Setting("Bonus when accepted", Help = "For the creator of a new proposal; revivals only get the cost back.", Unit = "points", Min = 0, Max = 1_000_000)]
    public double AcceptBonus { get; init; } = 20;

    [Setting("Votes needed", Help = "Accepted once 👍 lead 👎 by this many (or reach this many, with down votes off).", Min = 1, Max = 1000)]
    public int VoteMargin { get; init; } = 5;

    [Setting("Down votes", Help = "Off: proposals have no 👎 button and win on 👍 alone. The same for emojis, stickers and sounds.")]
    public bool DownVotes { get; init; } = true;

    [Setting("Voting lasts", Unit = "days", Min = 1, Max = 60)]
    public int VoteDays { get; init; } = 3;

    [Setting("Royalty per use", Help = "For the creator each time someone else uses it.", Unit = "points", Min = 0, Max = 1000)]
    public double RoyaltyPerUse { get; init; } = 0.5;

    [Setting("Most royalties a day", Unit = "points", Min = 0, Max = 1_000_000)]
    public double RoyaltyDailyCap { get; init; } = 20;

    [Setting("Retire when unused for", Help = "Out of the library and off Discord, with the image kept so it can be revived. Never what members uploaded on Discord themselves.", Unit = "days", Min = 1, Max = 3650)]
    public int RetireAfterDays { get; init; } = 60;

    [Setting("In step with Discord's emojis and stickers", Help = "What members upload straight to Discord comes in with its uploader as maker (royalties, the page). One taken off Discord there stays in the library, off Discord until pinned.")]
    public bool FollowDiscord { get; init; } = true;

    [Setting("Discord's emoji and sticker lists show", Section = "Library", Help = "The library holds every accepted emoji and sticker; Discord's lists only so many. The other emoji are the bot's own, used with :name: (posted again under the member's name) or React with emoji; the other stickers are posted as their picture with /sticker send or sticker:name.", Choices = [EmojiListChoices.MostUsed + "=Around the pinned ones, the most used", EmojiListChoices.Managers + "=What managers pin there"])]
    public string EmojiList { get; init; } = EmojiListChoices.MostUsed;

    [Setting("Most used: uses counted over", Section = "Library", Unit = "days", Min = 1, Max = 365)]
    public int MostUsedDays { get; init; } = 30;

    [Setting("Most used: one needs this much more uses to replace another", Section = "Library", Help = "So the list doesn't churn under people's fingers.", Unit = "%", Min = 0, Max = 500)]
    public int SwapMargin { get; init; } = 25;

    [Setting("Most used: swaps a day at most", Section = "Library", Help = "For each list. Filling a free slot isn't a swap.", Unit = "swaps", Min = 0, Max = 48)]
    public int SwapsPerDay { get; init; } = 2;

    [Setting("Most used: a newly accepted one holds a free slot for", Section = "Library", Help = "To earn uses; 0 gives new ones no head start.", Unit = "days", Min = 0, Max = 90)]
    public int TrialDays { get; init; } = 7;

    [Setting("Emoji by :name: in messages", Section = "Using emoji", Help = "A message with :name: for an emoji the member couldn't use there (an animated one without Nitro, one from another server) is posted again under their name with it (needs the Posting for members module). Members can turn it off for theirs on their You page.")]
    public bool ByName { get; init; } = true;

    [Setting("Emoji from other servers", Section = "Using emoji", Choices = [OtherServers.None + "=Only this server's", OtherServers.Shared + "=Also from other servers the member shares with the bot", OtherServers.All + "=Also from every server the bot is in"])]
    public string EmojiFrom { get; init; } = OtherServers.Shared;

    [Setting("Stickers by sticker:name in messages", Section = "Using emoji", Help = "A message with sticker:name in it is posted again under the member's name with that library sticker's picture, any of them, on Discord's list or not (needs the Posting for members module). /sticker send does the same.")]
    public bool StickersByName { get; init; } = true;

    [Setting("Big emoji by big:name in messages", Section = "Using emoji", Help = "A message with big:name in it is posted again under the member's name with that emoji as a large picture (any emoji :name: could use). /emoji big does the same.")]
    public bool BigEmoji { get; init; } = true;

    [Setting("React with any emoji through the bot", Section = "Using emoji", Help = "Right-click a message → Apps → React with emoji: the bot reacts with it, the member presses it to add theirs, and the bot's goes.")]
    public bool ReactThroughBot { get; init; } = true;
}
