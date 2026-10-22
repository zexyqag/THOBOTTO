using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using THOBOTTO.Helpers;
using THOBOTTO.Voice.Speaking;

namespace THOBOTTO.Tests;

// With the ffmpeg on the path (the dev container's is the image's build); nothing where there is none.
public class FfmpegFilterTests
{
    private static string? Ffmpeg => Environment.GetEnvironmentVariable("PATH")!.Split(':').Select(d => Path.Combine(d, "ffmpeg")).FirstOrDefault(File.Exists);

    private static FfmpegFilter Filter() => new(new ConfigurationBuilder().Build(), NullLogger<FfmpegFilter>.Instance);

    [Fact]
    public async Task Every_allowed_filter_is_in_the_build()
    {
        if (Ffmpeg is null)
            return;
        using var ffmpeg = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Ffmpeg, ["-hide_banner", "-filters"]) { RedirectStandardOutput = true })!;
        var listed = (await ffmpeg.StandardOutput.ReadToEndAsync()).Split('\n').Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(p => p.Length > 2).Select(p => p[1]).ToHashSet();
        Assert.All(VoiceFilters.Allowed, f => Assert.Contains(f, listed));
    }

    [Fact]
    public async Task A_chain_runs_and_a_bad_one_says_why()
    {
        if (Ffmpeg is null)
            return;
        var filter = Filter();
        Assert.Null(await filter.CheckAsync("volume=0.5"));
        foreach (var (name, chain) in VoiceFilters.Presets)
            Assert.Null(await filter.CheckAsync(chain));
        var problem = await filter.CheckAsync("aecho=abc");
        Assert.NotNull(problem);
        Assert.DoesNotContain("0x", problem);
        var loud = new Spoken([.. Enumerable.Repeat((short)10000, 22050)], 22050);
        var halved = await filter.ApplyAsync(loud, "volume=0.5", CancellationToken.None);
        Assert.NotNull(halved);
        Assert.Equal(22050, halved.Rate);
        Assert.InRange(halved.Samples.Length, 22000, 22100);
        Assert.InRange(halved.Samples[11025], 4900, 5100);
    }

    [Fact]
    public async Task A_chain_that_goes_on_is_cut()
    {
        if (Ffmpeg is null)
            return;
        Assert.StartsWith("The filters go on for more than", await Filter().CheckAsync("asetrate=100"));
    }
}
