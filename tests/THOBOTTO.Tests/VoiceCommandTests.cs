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
    [InlineData("Eaves, Playa Thunderstruck,", "Jeeves", VoiceIntent.Play, "thunderstruck")]
    [InlineData("Jeeves, plays ABBA", "Jeeves", VoiceIntent.Play, "abba")]
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

public class WhisperWindowTests
{
    [Theory]
    [InlineData(16_000 * 2, 256)]
    [InlineData(16_000 * 8, 500)]
    [InlineData(16_000 * 40, 1500)]
    public void The_window_fits_the_sentence(int samples, int steps)
        => Assert.Equal(steps, THOBOTTO.Listening.LocalWhisper.AudioContext(samples));
}

public class ArtistHintTests
{
    [Theory]
    [InlineData("ABBA - Topic", "ABBA")]
    [InlineData("ABBAVEVO", "ABBA")]
    [InlineData("Queen", "Queen")]
    public void Channel_names_become_artists(string channel, string artist)
        => Assert.Equal(artist, THOBOTTO.Listening.VoiceEars.CleanArtist(channel));
}

public class NoiseTests
{
    private static short[] Tone(double seconds, double loudness)
        => Enumerable.Range(0, (int)(seconds * 48_000)).Select(i => (short)(loudness * Math.Sin(i * 0.1))).ToArray();

    [Fact]
    public void Speech_goes_on() => Assert.True(THOBOTTO.Listening.VoiceEars.Speechlike(Tone(1.0, 3000)));

    [Fact]
    public void Quiet_doesnt() => Assert.False(THOBOTTO.Listening.VoiceEars.Speechlike(Tone(1.0, 100)));

    [Fact]
    public void Clicks_dont()
    {
        // 2 s, loud for one frame in ten.
        var samples = Tone(2.0, 100);
        for (var f = 0; f < 100; f += 10)
            for (var i = f * 960; i < (f + 1) * 960; i++)
                samples[i] = (short)(8000 * Math.Sin(i * 0.3));
        Assert.False(THOBOTTO.Listening.VoiceEars.Speechlike(samples));
    }

    [Theory]
    [InlineData("[BLANK_AUDIO]", "")]
    [InlineData("(keyboard clicking)", "")]
    [InlineData("Jeeves, skip [BLANK_AUDIO]", "Jeeves, skip")]
    public void Whispers_noise_notes_are_nothing(string heard, string left)
        => Assert.Equal(left, THOBOTTO.Listening.VoiceEars.WithoutNoises(heard));

    [Theory]
    [InlineData("aba", "ABBA")]
    [InlineData("acdc", "AC/DC")]
    [InlineData("thunderstruck", null)]
    public void Close_names_become_the_artist(string asked, string? artist)
        => Assert.Equal(artist, THOBOTTO.Listening.VoiceEars.ClosestArtist(asked, ["ABBA", "AC/DC", "Queen"]));
}
