using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
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
    public async Task Build_error_text_is_truncated_and_stripped_of_control_characters()
    {
        var long_ = "evil\n\u001b[31m" + new string('x', 5000);
        var payload = System.Text.Json.JsonSerializer.Serialize(new { error = "build_failed", message = long_ });
        var (client, _) = Make((_, _) => Json(HttpStatusCode.UnprocessableEntity, payload));
        var ex = await Assert.ThrowsAsync<BuilderException>(() => client.BuildAsync("clamm", "testnet", null));
        Assert.True(ex.Message.Length < 400);
        Assert.DoesNotContain('\n', ex.Message);
        Assert.DoesNotContain('\u001b', ex.Message);
        Assert.DoesNotContain("evil", ex.PublicMessage);
    }

    // BCHAT-016: a sidecar response above the client's buffer limit is a BuilderException, not an unhandled transport error.
    [Fact]
    public async Task An_oversized_response_is_refused_as_a_BuilderException_for_build_read_and_query()
    {
        var stub = new Stub((_, _) => Json(HttpStatusCode.OK, "{\"transactions\":[\"" + new string('A', 5000) + "\"],\"data\":{},\"x\":1}"));
        var options = new BuildersOptions { BaseAddress = new Uri("http://builders:3100"), InternalSecret = Secret };
        var client = new BuildersClient(new HttpClient(stub) { MaxResponseContentBufferSize = 1024 }, options);

        await Assert.ThrowsAsync<BuilderException>(() => client.BuildAsync("any", "testnet", new { }));
        await Assert.ThrowsAsync<BuilderException>(() => client.ReadAsync("any", "testnet", new { }));
        var e = await Assert.ThrowsAsync<BuilderException>(() => client.QueryAsync("any", "testnet", new { }));
        Assert.Contains("too large", e.Message);
    }

    // BCHAT-016: the backend does not buffer an unbounded response from the sidecar.
    [Fact]
    public async Task AddBuildersClient_bounds_the_response_it_buffers()
    {
        var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Builders:BaseAddress"] = "http://builders:3100", ["Builders:InternalSecret"] = "s3cret-value",
        }).Build();
        await using var sp = new ServiceCollection().AddBuildersClient(cfg).BuildServiceProvider();
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IBuildersClient));
        Assert.Equal(BuildersServiceCollectionExtensions.MaxResponseBytes, http.MaxResponseContentBufferSize);
        Assert.True(http.MaxResponseContentBufferSize <= 8 * 1024 * 1024);
    }

    [Fact]
    public void AddBuildersClient_registers_client_and_requires_secret()
    {
        var good = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Builders:BaseAddress"] = "http://builders:3100", ["Builders:InternalSecret"] = "s3cret-value",
        }).Build();
        var sp = new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddBuildersClient(good).BuildServiceProvider();
        Assert.NotNull(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IBuildersClient>(sp));

        var noSecret = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Builders:BaseAddress"] = "http://builders:3100",
        }).Build();
        Assert.Throws<InvalidOperationException>(() => { new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddBuildersClient(noSecret); });
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
