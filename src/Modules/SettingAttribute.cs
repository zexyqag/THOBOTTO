namespace THOBOTTO.Modules;

public enum SettingKind
{
    // From the property's type: a switch, a number, text, a list.
    Auto,
    TextChannel,
    Category,
    TimeZone,
    Role,
    // Several lines of text.
    LongText,
    // Stored in bytes, shown in megabytes.
    Megabytes,
}

// Marks a settings property the web panel shows, with how it reads there. Properties without it
// stay out of the panel (e.g. ones only a command can change safely).
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingAttribute(string label) : Attribute
{
    public string Label { get; } = label;

    public string? Help { get; init; }

    // A heading the setting is shown under, for long pages.
    public string? Section { get; init; }

    public string? Unit { get; init; }

    public double Min { get; init; } = double.MinValue;

    public double Max { get; init; } = double.MaxValue;

    public SettingKind Kind { get; init; }

    // For text with fixed choices: "value=Label" pairs.
    public string[]? Choices { get; init; }
}
