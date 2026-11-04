using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using THOBOTTO.Links;

namespace THOBOTTO.Tests;

public class PreviewCheckTests
{
    private readonly Dictionary<string, (string Type, string Body)> _served = [];

    private PreviewCheck Check() => new(new HttpClient(new Handler(_served)), TimeProvider.System, NullLogger<PreviewCheck>.Instance);

    [Fact]
    public async Task A_page_with_a_picture_a_post_as_activitypub_or_the_video_itself_previews()
    {
        _served["https://a.test/1"] = ("text/html", """<meta property="og:title" content="x"><meta property="og:image" content="https://a.test/p.jpg">""");
        _served["https://fx.test/1"] = ("text/html", """<meta property="og:title" content="x"><link href='https://fx.test/users/a/statuses/1' rel='alternate' type='application/activity+json'>""");
        _served["https://kk.test/1"] = ("video/mp4", "");

        Assert.Equal("https://a.test/1", await Check().FirstShowingAsync("a.test", ["https://a.test/1"]));
        Assert.Equal("https://fx.test/1", await Check().FirstShowingAsync("a.test", ["https://fx.test/1"]));
        Assert.Equal("https://kk.test/1", await Check().FirstShowingAsync("a.test", ["https://kk.test/1"]));
    }

    [Fact]
    public async Task A_service_that_answers_with_only_a_title_is_passed_over()
    {
        _served["https://gone.test/1"] = ("text/html", """<meta property="og:title" content="vxReddit">""");
        _served["https://ok.test/1"] = ("text/html", """<meta name="twitter:player" content="https://ok.test/v">""");

        Assert.Equal("https://ok.test/1", await Check().FirstShowingAsync("a.test", ["https://gone.test/1", "https://missing.test/1", "https://ok.test/1"]));
    }

    private sealed class Handler(Dictionary<string, (string Type, string Body)> served) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!served.TryGetValue(request.RequestUri!.ToString(), out var page))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(page.Body)) };
            response.Content.Headers.ContentType = new(page.Type);
            return Task.FromResult(response);
        }
    }
}
