using THOBOTTO.Listening;

namespace THOBOTTO.Tests;

public class VoiceCommandTests
{
    private static readonly string[] Names = ["Jeeves", "DJ Volume"];

    [Theory]
    // As Whisper heard them in testing.
    [InlineData("Jeave, skip this song.", "Jeeves", VoiceIntent.Skip, null)]
    [InlineData("Jeave, play thunderstruck IACBC.", "Jeeves", VoiceIntent.Play, "thunderstruck iacbc")]
    [InlineData("Hey DJ Volume, turn it up!", "DJ Volume", VoiceIntent.Louder, null)]
    [InlineData("Jeeves, can you pause the music please", "Jeeves", VoiceIntent.Pause, null)]
    [InlineData("Jeeves volume 40", "Jeeves", VoiceIntent.Volume, "40")]
    [InlineData("Jeeves, what's playing right now?", "Jeeves", VoiceIntent.NowPlaying, null)]
    [InlineData("Jeeves, put on some ABBA", "Jeeves", VoiceIntent.Play, "some abba")]
    [InlineData("Jeeves, make me a sandwich", "Jeeves", VoiceIntent.Unknown, null)]
    [InlineData("Jeeves, queue Hey Jude", "Jeeves", VoiceIntent.Queue, "hey jude")]
    [InlineData("Jeeves, add some ABBA to the queue", "Jeeves", VoiceIntent.Queue, "some abba")]
    [InlineData("Jeeves, play Hey Jude next", "Jeeves", VoiceIntent.QueueFirst, "hey jude")]
    [InlineData("Jeeves, queue Thunderstruck first", "Jeeves", VoiceIntent.QueueFirst, "thunderstruck")]
    [InlineData("Jeeves, play Hey Jude now", "Jeeves", VoiceIntent.Play, "hey jude")]
    [InlineData("Jeeves, cue Hey Jude", "Jeeves", VoiceIntent.Queue, "hey jude")]
    [InlineData("Jeeves, quote that", "Jeeves", VoiceIntent.Quote, "that")]
    [InlineData("Jeeves, quote me!", "Jeeves", VoiceIntent.Quote, "me")]
    [InlineData("Jeeves, quote the last three lines", "Jeeves", VoiceIntent.Quote, "the last three lines")]
    public void Commands_addressed_to_a_voice_are_understood(string heard, string name, VoiceIntent intent, string? argument)
    {
        var command = VoiceCommandParser.Parse(heard, Names);
        Assert.NotNull(command);
        Assert.Equal((name, intent, argument), (command.Name, command.Intent, command.Argument));
    }

    [Theory]
    [InlineData("What's playing right now")]
    [InlineData("skip this song")]
    [InlineData("I'm going to play some games")]
    [InlineData("Jesus, that was loud")]
    public void Sentences_not_addressed_to_a_voice_are_ignored(string heard)
        => Assert.Null(VoiceCommandParser.Parse(heard, Names));
}

public class VoiceCommandNearMissTests
{
    [Theory]
    // "what's playing", as heard in testing.
    [InlineData("Jeave, while playing.", VoiceIntent.NowPlaying)]
    [InlineData("Jeeves, turn it dawn", VoiceIntent.Quieter)]
    [InlineData("Jeeves, skip the song", VoiceIntent.Skip)]
    public void Near_misses_of_a_command_count(string heard, VoiceIntent intent)
        => Assert.Equal(intent, VoiceCommandParser.Parse(heard, ["Jeeves"])!.Intent);

    // Heard in testing for "turn it down": as near "turn it up", so better asked again than guessed.
    [Fact]
    public void A_near_miss_between_two_commands_isnt_guessed()
        => Assert.Equal(VoiceIntent.Unknown, VoiceCommandParser.Parse("Jeave, turn it out.", ["Jeeves"])!.Intent);
}
