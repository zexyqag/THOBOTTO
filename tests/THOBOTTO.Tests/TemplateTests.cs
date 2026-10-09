using System.Text.RegularExpressions;

using THOBOTTO.Helpers;

namespace THOBOTTO.Tests;

public class TemplateTests
{
    [Fact]
    public void Names_are_unique()
        => Assert.Equal(PersonalityFile.Templates.Count, PersonalityFile.Templates.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());

    [Fact]
    public void Every_template_speaks_at_every_moment()
    {
        foreach (var template in PersonalityFile.Templates.Append(PersonalityFile.Plain))
            foreach (var moment in Moments.All)
                Assert.True(template.Lines.GetValueOrDefault(moment) is { Count: > 0 }, $"{template.Name} has no {moment} line");
    }

    [Fact]
    public void Lines_only_use_known_placeholders()
    {
        string[] known = ["{track}", "{user}", "{channel}", "{helper}", "{period}"];
        foreach (var template in PersonalityFile.Templates.Append(PersonalityFile.Plain))
            foreach (var line in template.Lines.Values.SelectMany(p => p))
                foreach (Match placeholder in Regex.Matches(line, @"\{\w+\}"))
                    Assert.Contains(placeholder.Value, known);
    }
}
