using System.Net;
using System.Net.Http;
using System.Text;

namespace OpenPin.Tests.Translate;

/// <summary>Records requests and answers them from a callback. No network is ever used.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, int, HttpResponseMessage> _respond;

    public FakeHandler(Func<HttpRequestMessage, string, int, HttpResponseMessage> respond) => _respond = respond;

    public FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : this((r, b, _) => respond(r, b)) { }

    public List<(HttpMethod Method, Uri Uri, string? Auth, string Body)> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));
        return _respond(request, body, Requests.Count);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public HttpClient Client() => new(this, disposeHandler: false);
}
