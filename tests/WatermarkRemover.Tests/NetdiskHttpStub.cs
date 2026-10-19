using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace WatermarkRemover.Tests;

/// <summary>
/// Scriptable HTTP handler for the netdisk tests. Every call is recorded so tests can assert on
/// request bodies and call counts (e.g. "the token endpoint was hit exactly once").
/// </summary>
internal sealed class NetdiskHttpStub : HttpMessageHandler
{
    public Func<StubCall, HttpResponseMessage> Responder { get; set; } =
        _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

    public List<StubCall> Calls { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
        var call = new StubCall(
            request.Method.Method,
            request.RequestUri!.ToString(),
            body,
            request.Headers.Authorization?.Parameter);
        Calls.Add(call);
        return Task.FromResult(Responder(call));
    }

    public int CountCalls(Func<StubCall, bool> predicate) => Calls.Count(predicate);

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>Builds an unsigned JWT whose payload carries only the given claims — enough for sub checks.</summary>
    public static string FakeJwt(object payload)
    {
        static string Encode(object value) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');

        return Encode(new { alg = "none", typ = "JWT" }) + "." + Encode(payload) + ".sig";
    }

    public sealed record StubCall(string Method, string Url, string? Body, string? Authorization);
}
