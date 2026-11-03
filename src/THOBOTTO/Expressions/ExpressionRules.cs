using THOBOTTO.Modules;

namespace THOBOTTO.Expressions;

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

    [Setting("Retire when unused for", Help = "Deleted, with the image kept so it can be revived.", Unit = "days", Min = 1, Max = 3650)]
    public int RetireAfterDays { get; init; } = 60;

    [Setting("Replace the least used when full", Help = "Otherwise wait for a free slot.")]
    public bool ReplaceLeastUsed { get; init; }

    [Setting("In step with Discord's emojis and stickers", Help = "What members upload straight to Discord comes in with its uploader as maker (royalties, the page), and one deleted there is marked retired. The bot never retires what it didn't make.")]
    public bool FollowDiscord { get; init; } = true;

    [Setting("Emoji by :name: in messages", Section = "Using emoji", Help = "A message with :name: for an emoji the member couldn't use there (an animated one without Nitro, one from another server) is posted again under their name with it (needs the Posting for members module). Members can turn it off for theirs on their You page.")]
    public bool ByName { get; init; } = true;

    [Setting("Emoji from other servers", Section = "Using emoji", Choices = [OtherServers.None + "=Only this server's", OtherServers.Shared + "=Also from other servers the member shares with the bot", OtherServers.All + "=Also from every server the bot is in"])]
    public string EmojiFrom { get; init; } = OtherServers.Shared;

    [Setting("React with any emoji through the bot", Section = "Using emoji", Help = "Right-click a message → Apps → React with emoji: the bot reacts with it, the member presses it to add theirs, and the bot's goes.")]
    public bool ReactThroughBot { get; init; } = true;
}
