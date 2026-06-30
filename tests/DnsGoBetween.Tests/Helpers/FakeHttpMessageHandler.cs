using System.Net;
using System.Text;

namespace DnsGoBetween.Tests.Helpers;

/// <summary>
/// Test-only HttpMessageHandler that returns canned responses based on a user-supplied
/// responder function. Records every request for later assertion.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    public List<HttpRequestMessage> Requests { get; } = new();
    public List<string> RequestBodies { get; } = new();

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (request.Content is not null)
        {
            RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
        }
        else
        {
            RequestBodies.Add(string.Empty);
        }
        return _responder(request);
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Xml(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };
}
