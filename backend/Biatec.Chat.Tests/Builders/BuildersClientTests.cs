using System.Net;
using System.Text;
using Biatec.Chat.Tools.Builders;

namespace Biatec.Chat.Tests.Builders;

public sealed class BuildersClientTests
{
    private const string Secret = "unit-test-secret-0123";

    private sealed class Stub(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (BuildersClient Client, Stub Stub) Make(Func<HttpRequestMessage, string?, HttpResponseMessage> respond)
    {
        var stub = new Stub(respond);
        var options = new BuildersOptions { BaseAddress = new Uri("http://builders:3100"), InternalSecret = Secret };
        return (new BuildersClient(new HttpClient(stub), options), stub);
    }

    [Fact]
    public async Task Health_calls_slash_health_and_reads_ok()
    {
        var (client, stub) = Make((_, _) => Json(HttpStatusCode.OK, """{"status":"ok"}"""));
        Assert.True(await client.IsHealthyAsync());
        Assert.Equal("http://builders:3100/health", stub.Seen.Single().Request.RequestUri!.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}")]
    [InlineData(HttpStatusCode.OK, """{"status":"starting"}""")]
    [InlineData(HttpStatusCode.OK, "not json")]
    public async Task Health_is_false_when_not_ok(HttpStatusCode code, string body)
    {
        var (client, _) = Make((_, _) => Json(code, body));
        Assert.False(await client.IsHealthyAsync());
    }

    [Fact]
    public async Task Health_is_false_when_unreachable()
    {
        var (client, _) = Make((_, _) => throw new HttpRequestException("refused"));
        Assert.False(await client.IsHealthyAsync());
    }

    [Fact]
    public async Task Build_sends_secret_network_and_params_and_returns_transactions()
    {
        var (client, stub) = Make((_, _) => Json(HttpStatusCode.OK, """{"transactions":["AAEC","AwQF"]}"""));
        var result = await client.BuildAsync("clamm", "testnet", new { pool = 1 });

        Assert.Equal(["AAEC", "AwQF"], result.Transactions);
        var (request, body) = stub.Seen.Single();
        Assert.Equal("http://builders:3100/v1/build/clamm", request.RequestUri!.ToString());
        Assert.Equal(Secret, request.Headers.GetValues(BuildersOptions.SecretHeader).Single());
        Assert.Equal("""{"network":"testnet","params":{"pool":1}}""", body);
    }

    [Fact]
    public async Task Build_failure_surfaces_status_and_message_but_not_the_secret()
    {
        var (client, _) = Make((_, _) => Json(HttpStatusCode.UnprocessableEntity, """{"error":"build_failed","message":"pool not found"}"""));
        var e = await Assert.ThrowsAsync<BuilderException>(() => client.BuildAsync("clamm", "testnet", null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, e.Status);
        Assert.Contains("pool not found", e.Message);
        Assert.DoesNotContain(Secret, e.Message);
    }

    [Fact]
    public async Task Build_rejects_empty_result()
    {
        var (client, _) = Make((_, _) => Json(HttpStatusCode.OK, """{"transactions":[]}"""));
        await Assert.ThrowsAsync<BuilderException>(() => client.BuildAsync("safe", "testnet", null));
    }
}
