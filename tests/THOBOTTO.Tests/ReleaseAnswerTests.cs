using System.Net;

using THOBOTTO.Panel.Pages;

namespace THOBOTTO.Tests;

public class ReleaseAnswerTests
{
    private static readonly DateTimeOffset Asked = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_answer_holds_for_an_hour()
    {
        var answer = new ReleaseAnswer(Asked, "0.1.0", "\"abc\"", null);
        Assert.True(answer.Holds(Asked.AddMinutes(59)));
        Assert.False(answer.Holds(Asked.AddHours(1)));
    }

    [Fact]
    public void A_release_is_remembered_with_its_etag()
        => Assert.Equal(new ReleaseAnswer(Asked, "0.2.0", "\"abc\"", null), ReleaseAnswer.From(null, HttpStatusCode.OK, "0.2.0", "\"abc\"", Asked));

    [Fact]
    public void Not_modified_keeps_the_release_known()
    {
        var before = new ReleaseAnswer(Asked, "0.1.0", "\"abc\"", null);
        Assert.Equal(before with { AskedAt = Asked.AddHours(2) }, ReleaseAnswer.From(before, HttpStatusCode.NotModified, null, null, Asked.AddHours(2)));
    }

    [Fact]
    public void No_release_yet_is_remembered_too()
        => Assert.Equal(new ReleaseAnswer(Asked, null, null, null), ReleaseAnswer.From(null, HttpStatusCode.NotFound, null, null, Asked));

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public void A_refusal_is_worded_plainly_and_keeps_what_was_known(HttpStatusCode status)
    {
        var before = new ReleaseAnswer(Asked, "0.1.0", "\"abc\"", null);
        var after = ReleaseAnswer.From(before, status, null, null, Asked.AddHours(2));
        Assert.Equal("GitHub asks this address to slow down; asked again later.", after.Problem);
        Assert.Equal(("0.1.0", "\"abc\"", Asked.AddHours(2)), (after.Latest, after.ETag, after.AskedAt));
    }
}
