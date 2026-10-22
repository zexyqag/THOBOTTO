using THOBOTTO.Helpers;
using THOBOTTO.Voice.Speaking;

namespace THOBOTTO.Tests;

public class EngineChoiceTests
{
    [Theory]
    [InlineData("espeak", "kokoro", "espeak")]
    [InlineData("piper", "cloud", "piper")]
    [InlineData("kokoro", "piper", "piper")]
    [InlineData("cloud", "cloud", "cloud")]
    [InlineData(null, "kokoro", "kokoro")]
    public void A_preference_counts_when_the_engine_is_here(string? preferred, string server, string spoken)
        => Assert.Equal(spoken, HelperVoices.Choose(preferred, server));

    [Fact]
    public void Espeaks_voice_listing_is_read_by_language()
    {
        const string listing = """
            Pty Language       Age/Gender VoiceName          File                 Other Languages
             2  en-gb           --/M      English_(Great_Britain) gmw/en               (en 2)
             2  en-us           --/M      English_(America)  gmw/en-US            (en 3)
             5  en-gb-x-rp      --/M      English_(Received_Pronunciation) gmw/en-GB-x-rp       (en-gb 4)(en 5)
             5  en-us           --/M      us-mbrola-2        mb/mb-us2            (en 7)
            """;
        Assert.Equal(["en-gb", "en-gb-x-rp", "en-us"], EspeakVoices.ParseVoices(listing));
    }

    [Fact]
    public void A_wav_from_a_pipe_gives_its_samples_and_rate()
    {
        var wav = new List<byte>();
        wav.AddRange("RIFF"u8.ToArray()); wav.AddRange(BitConverter.GetBytes(0xFFFFFFFFu)); wav.AddRange("WAVE"u8.ToArray());
        wav.AddRange("fmt "u8.ToArray()); wav.AddRange(BitConverter.GetBytes(16u));
        wav.AddRange(BitConverter.GetBytes((short)1)); wav.AddRange(BitConverter.GetBytes((short)1)); wav.AddRange(BitConverter.GetBytes(22050));
        wav.AddRange(BitConverter.GetBytes(44100)); wav.AddRange(BitConverter.GetBytes((short)2)); wav.AddRange(BitConverter.GetBytes((short)16));
        wav.AddRange("data"u8.ToArray()); wav.AddRange(BitConverter.GetBytes(0xFFFFFFFFu));
        foreach (var sample in new short[] { 1, -2, 300 })
            wav.AddRange(BitConverter.GetBytes(sample));
        var read = Pcm.FromWav([.. wav]);
        Assert.NotNull(read);
        Assert.Equal(22050, read.Value.Rate);
        Assert.Equal([1, -2, 300], read.Value.Samples);
    }

    [Fact]
    public void A_file_names_a_known_engine_or_is_refused()
    {
        Assert.Null(PersonalityFile.Parse("""{ "name": "X", "lines": {}, "engine": "espeak" }""").Problem);
        Assert.StartsWith("\"dectalk\" isn't a speech engine", PersonalityFile.Parse("""{ "name": "X", "lines": {}, "engine": "dectalk" }""").Problem);
        Assert.Equal("espeak", PersonalityFile.Templates.Single(t => t.Name == "Unit 404").Engine);
    }

    // Only where eSpeak NG is installed (the dev container); elsewhere there's nothing to check.
    [Fact]
    public async Task Espeak_speaks_when_installed()
    {
        if (!Environment.GetEnvironmentVariable("PATH")!.Split(':').Any(d => File.Exists(Path.Combine(d, "espeak-ng"))))
            return;
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var espeak = new EspeakVoices(config, Microsoft.Extensions.Logging.Abstractions.NullLogger<EspeakVoices>.Instance);
        Assert.Contains("en-us", await espeak.VoicesAsync());
        var spoken = await espeak.SayAsync("Unit four oh four online.", "en-us+m3", 1, CancellationToken.None);
        Assert.NotNull(spoken);
        Assert.Equal(22050, spoken.Rate);
        Assert.InRange(spoken.Length.TotalSeconds, 0.8, 4);
    }
}

public class PreviewEffectsTests
{
    [Fact]
    public void The_pages_effect_fields_are_read()
        => Assert.Equal(new VoiceEffects(Speed: 1.2, Pitch: -3), VoicePreview.Effects("speed:1.2,pitch:-3,static:0,glitch:0,bitcrush:16,robot:0,echo:0"));

    [Fact]
    public void Nothing_or_nonsense_means_the_saved_effects()
    {
        Assert.Null(VoicePreview.Effects(""));
        Assert.Null(VoicePreview.Effects("pitch:99"));
        Assert.Null(VoicePreview.Effects("pitch=3"));
    }
}
