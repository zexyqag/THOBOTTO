using System.Text.RegularExpressions;

using THOBOTTO.Helpers;

namespace THOBOTTO.Tests;

public class TemplateTests
{
    [Fact]
    public void Names_are_unique()
        => Assert.Equal(Template.All.Count, Template.All.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());

    [Fact]
    public void Every_template_speaks_at_every_moment()
    {
        foreach (var template in Template.All.Append(Template.Plain))
            foreach (var moment in Moments.All)
                Assert.True(template.Phrases.GetValueOrDefault(moment) is { Length: > 0 }, $"{template.Name} has no {moment} line");
    }

    [Fact]
    public void Lines_only_use_known_placeholders()
    {
        string[] known = ["{track}", "{user}", "{channel}", "{helper}"];
        foreach (var template in Template.All.Append(Template.Plain))
            foreach (var line in template.Phrases.Values.SelectMany(p => p))
                foreach (Match placeholder in Regex.Matches(line, @"\{\w+\}"))
                    Assert.Contains(placeholder.Value, known);
    }
}
