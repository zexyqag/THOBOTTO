using THOBOTTO.Helpers;

namespace THOBOTTO.Tests;

public class VoiceFiltersTests
{
    [Fact]
    public void Names_come_in_order_and_quotes_keep_their_commas()
    {
        Assert.Equal(["highpass", "lowpass", "aecho"], VoiceFilters.Names("highpass=f=300,lowpass=f=3400,aecho=0.8:0.6:40:0.3"));
        Assert.Equal(["afftfilt", "volume"], VoiceFilters.Names("afftfilt=real='hypot(re,im)':imag='hypot(re,im)',volume=2"));
        Assert.Equal(["volume"], VoiceFilters.Names("volume"));
    }

    [Fact]
    public void Every_preset_passes_and_the_rate_is_filled_in()
    {
        foreach (var (name, chain) in VoiceFilters.Presets)
            Assert.Null(VoiceFilters.Problem(chain));
        Assert.Equal("asetrate=22050*0.85,aresample=22050", VoiceFilters.WithRate("asetrate={rate}*0.85,aresample={rate}", 22050));
    }

    [Fact]
    public void A_filter_that_reads_or_branches_is_refused()
    {
        Assert.StartsWith("\"amovie\" isn't a filter that may be used here", VoiceFilters.Problem("amovie=/etc/hostname"));
        Assert.StartsWith("\"afir\" isn't a filter", VoiceFilters.Problem("volume=2,afir"));
        Assert.StartsWith("The filters are one straight chain", VoiceFilters.Problem("[a]volume=2[b];[b]aecho"));
        Assert.StartsWith("The filters are one straight chain", VoiceFilters.Problem("volume=2;rm -rf"));
        Assert.Equal("A quote in the filters isn't closed.", VoiceFilters.Problem("afftfilt=real='hypot(re,im)"));
        Assert.StartsWith("The filters may use", VoiceFilters.Problem("volume=2&rm"));
        Assert.StartsWith("The filters may use", VoiceFilters.Problem("volume=$HOME"));
        Assert.Equal("A filter in the chain has no name.", VoiceFilters.Problem("volume=2,,aecho"));
        Assert.StartsWith("The filters are longer than", VoiceFilters.Problem(new string('a', VoiceFilters.MaxLength + 1)));
        Assert.Null(VoiceFilters.Problem("volume=2, aecho=0.8:0.6:40:0.3"));
    }

    [Fact]
    public void A_file_carries_its_filters_and_a_bad_chain_is_refused()
    {
        var (file, problem) = PersonalityFile.Parse("""{ "name": "X", "lines": {}, "filters": " lowpass=f=3000 " }""");
        Assert.Null(problem);
        Assert.Equal("lowpass=f=3000", file!.Filters);
        Assert.Null(PersonalityFile.Parse("""{ "name": "X", "lines": {}, "filters": "  " }""").File!.Filters);
        Assert.StartsWith("\"amovie\" isn't a filter", PersonalityFile.Parse("""{ "name": "X", "lines": {}, "filters": "amovie=x" }""").Problem);
        Assert.Contains("\"filters\": \"lowpass=f=3000\"", file.ToJson());
    }
}
