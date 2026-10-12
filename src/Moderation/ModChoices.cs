using NetCord.Services.ApplicationCommands;

namespace THOBOTTO.Moderation;

public enum DeleteMessages
{
    None,
    LastHour,
    LastDay,
    LastWeek,
}

public enum EscalationChoice
{
    Timeout,
    Kick,
    Ban,
    [SlashCommandChoice(Name = "Nothing (remove this step)")]
    Nothing,
}

public enum ListChange
{
    Add,
    Remove,
}
