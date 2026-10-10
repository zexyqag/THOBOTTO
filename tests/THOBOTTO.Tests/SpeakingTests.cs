using THOBOTTO.Speaking;

namespace THOBOTTO.Tests;

public class SpeakingTests
{
    [Theory]
    [InlineData("▶️ [**Thunderstruck**](https://example.com/x)", "Thunderstruck")]
    [InlineData("🎙️ <@123> · You have **16 points**.", "You have 16 points.")]
    [InlineData("Skipped. <:pepe:123456> See https://example.com", "Skipped. See")]
    [InlineData("-# small print", "small print")]
    public void Chat_lines_become_speech(string text, string spoken)
        => Assert.Equal(spoken, SpeakableText.From(text, 300));

    [Fact]
    public void Long_lines_are_cut_at_a_sentence()
        => Assert.Equal("First sentence here.", SpeakableText.From("First sentence here. Second sentence that goes on and on.", 30));

    [Fact]
    public void Speech_becomes_48k_stereo()
    {
        var discord = Pcm.ToDiscord(new([0, 1000, 2000], 24_000));
        Assert.Equal(12, discord.Length);
        Assert.Equal(discord[0], discord[1]);
        Assert.Equal((short)500, discord[2]);
    }

    [Fact]
    public void Wav_has_a_header_and_the_samples()
        => Assert.Equal(44 + 6, Pcm.Wav(new([1, 2, 3], 22_050)).Length);
}

public class SpeechApiTests
{
    [Theory]
    [InlineData("""{"voices":[{"id":"bm_george","name":"bm_george","overall_grade":"C"},{"id":"af_heart","name":"af_heart"}],"default_voice":"af_heart"}""")]
    [InlineData("""{"voices":["bm_george","af_heart"]}""")]
    public void Voice_lists_are_read_either_way(string json)
        => Assert.Equal(["af_heart", "bm_george"], SpeechApi.ParseVoices(System.Text.Json.JsonDocument.Parse(json).RootElement));
}
