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

    // The steps with the one at that many warnings replaced, or removed when there's no action.
    public static IReadOnlyList<EscalationStep> With(IReadOnlyList<EscalationStep> steps, int warnings, string? action, TimeSpan? duration)
    {
        var changed = steps.Where(s => s.Warnings != warnings).ToList();
        if (action is not null)
            changed.Add(new(warnings, action, action == EscalationActions.Kick ? null : (int?)duration?.TotalMinutes));
        return changed.OrderBy(s => s.Warnings).ToList();
    }

    public static string Describe(EscalationStep step)
        => $"{step.Warnings} warnings → {step.Action}{(step.Minutes is { } m ? $" for {Durations.Format(TimeSpan.FromMinutes(m))}" : step.Action == EscalationActions.Ban ? " for good" : "")}";
}
