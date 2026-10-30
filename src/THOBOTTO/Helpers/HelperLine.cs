namespace THOBOTTO.Helpers;

// A personality's line for a moment, filled in: as said (with the voice marks) and as shown in chat (without).
public sealed record HelperLine(string Said)
{
    public string Shown { get; } = VoiceMarks.Strip(Said);

    public override string ToString() => Shown;

    public static implicit operator string(HelperLine line) => line.Shown;
}
