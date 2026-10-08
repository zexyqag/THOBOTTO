namespace THOBOTTO.Moderation;

public static class EscalationActions
{
    public const string Timeout = "timeout";
    public const string Kick = "kick";
    public const string Ban = "ban";
}

// When a member reaches this many active warnings, the bot does this.
public sealed record EscalationStep(int Warnings, string Action, int? Minutes);

public static class Escalation
{
    // The step for exactly this many warnings, so each fires once, as the count reaches it.
    public static EscalationStep? StepFor(IReadOnlyList<EscalationStep> steps, int activeWarnings)
        => steps.FirstOrDefault(s => s.Warnings == activeWarnings);

    public static string Describe(EscalationStep step)
        => $"{step.Warnings} warnings → {step.Action}{(step.Minutes is { } m ? $" for {Durations.Format(TimeSpan.FromMinutes(m))}" : step.Action == EscalationActions.Ban ? " for good" : "")}";
}
