using THOBOTTO.Modules;

namespace THOBOTTO.Gate;

// What happens to someone joining in a raid, or with a too-new account.
public static class GateActions
{
    public const string Hold = "hold";
    public const string Timeout = "timeout";
    public const string Kick = "kick";
    public const string Alert = "alert";

    public static readonly string[] Choices = [Hold + "=Hold them in the waiting room", Timeout + "=Time them out", Kick + "=Kick them", Alert + "=Only tell the moderators"];
}

public static class VerifyModes
{
    public const string Off = "off";
    public const string Button = "button";
    public const string Question = "question";
    public const string Mods = "mods";
}

// Stored with SettingsStore under the module id.
public sealed record GateRules
{
    [Setting("Waiting role", Section = "Waiting room", Help = "Held and not-yet-verified members have it; it should only see the waiting channel. /setup gate prepare makes both.", Kind = SettingKind.Role)]
    public ulong? WaitingRoleId { get; init; }

    [Setting("Waiting channel", Section = "Waiting room", Help = "Where newcomers verify.", Kind = SettingKind.TextChannel)]
    public ulong? WaitingChannelId { get; init; }

    [Setting("Gate channel", Section = "Waiting room", Help = "Where moderators hear about raids and newcomers waiting to be let in.", Kind = SettingKind.TextChannel)]
    public ulong? ReviewChannelId { get; init; }

    [Setting("New members verify", Section = "Verification", Choices = [VerifyModes.Off + "=No: they're in at once", VerifyModes.Button + "=By agreeing to the rules (a button)", VerifyModes.Question + "=By answering a question", VerifyModes.Mods + "=When a moderator lets them in"])]
    public string Verify { get; init; } = VerifyModes.Button;

    [Setting("Role for verified members", Section = "Verification", Help = "Given on verifying, if your channels need one.", Kind = SettingKind.Role)]
    public ulong? VerifiedRoleId { get; init; }

    [Setting("Rules", Section = "Verification", Help = "Shown above the button in the waiting channel (/setup gate post puts it there again).", Kind = SettingKind.LongText)]
    public string Rules { get; init; } = "Welcome! Be kind to each other. Press the button to agree to the rules and come in.";

    [Setting("Question", Section = "Verification", Help = "For verifying by answering.")]
    public string? Question { get; init; }

    [Setting("Right answers", Section = "Verification", Help = "Separated by commas; capitals don't matter. Other answers go to the moderators.")]
    public IReadOnlyList<string> Answers { get; init; } = [];

    [Setting("A raid is", Section = "Raids", Help = "This many joins…", Unit = "joins (0: never)", Min = 0, Max = 500)]
    public int RaidJoins { get; init; } = 10;

    [Setting("…within", Section = "Raids", Unit = "seconds", Min = 5, Max = 3600)]
    public int RaidSeconds { get; init; } = 60;

    [Setting("During a raid, newcomers are", Section = "Raids", Choices = [GateActions.Hold + "=Held in the waiting room", GateActions.Timeout + "=Timed out", GateActions.Kick + "=Kicked", GateActions.Alert + "=Let in (moderators are told)"])]
    public string RaidAction { get; init; } = GateActions.Hold;

    [Setting("Ping on a raid", Section = "Raids", Kind = SettingKind.Role)]
    public ulong? RaidAlertRoleId { get; init; }

    [Setting("A raid ends after", Section = "Raids", Help = "Without anyone joining.", Unit = "minutes", Min = 1, Max = 1440)]
    public int RaidQuietMinutes { get; init; } = 10;

    [Setting("Pause invites during a raid", Section = "Raids", Help = "Discord's own pause: nobody new can join until it ends.")]
    public bool PauseInvites { get; init; } = true;

    [Setting("Raise the verification level during a raid", Section = "Raids", Help = "Discord's \"High\" level, set back when it ends.")]
    public bool RaiseVerification { get; init; } = true;

    [Setting("Let held members in when a raid ends", Section = "Raids", Help = "Otherwise they verify, or a moderator lets them in.")]
    public bool LetInAfterRaid { get; init; }

    [Setting("Accounts younger than", Section = "New accounts", Unit = "days (0: any age)", Min = 0, Max = 365)]
    public int MinAccountDays { get; init; }

    [Setting("…are", Section = "New accounts", Choices = [GateActions.Hold + "=Held in the waiting room", GateActions.Timeout + "=Timed out", GateActions.Kick + "=Kicked", GateActions.Alert + "=Let in (moderators are told)"])]
    public string YoungAction { get; init; } = GateActions.Hold;

    [Setting("Timeouts last", Section = "New accounts", Help = "For timing out raiders or new accounts.", Unit = "minutes", Min = 1, Max = 40320)]
    public int TimeoutMinutes { get; init; } = 60;
}
