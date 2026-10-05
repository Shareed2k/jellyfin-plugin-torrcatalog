using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jellyfin.Plugin.TorrCatalog.Tmdb;

namespace Jellyfin.Plugin.TorrCatalog.Tests;

/// <summary>One fake for every upstream (TMDB, Prowlarr, TorrServer): route on request host/path in Respond.</summary>
internal sealed class FakeHttp : HttpMessageHandler, IHttpClientFactory
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string> Bodies { get; } = [];

    public Func<HttpRequestMessage, string?, HttpResponseMessage> Respond { get; set; } = (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound);

    public HttpClient CreateClient(string name) =>
        new(this, disposeHandler: false) { BaseAddress = name == TmdbClient.HttpClientName ? TmdbClient.BaseAddress : null };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
        {
            Requests.Add(request);
            Bodies.Add(body ?? string.Empty);
        }

        return Respond(request, body);
    }

    public static HttpResponseMessage Snake(object body) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(body, options: new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }) };

    public static HttpResponseMessage Camel(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
}
