using System.Net;
using System.Text.Json.Nodes;
using Biatec.Chat.Api.Chat.SignIn;
using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Api.Hosting;
using Biatec.Chat.Api.Orchestration;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Core.Capabilities;
using Biatec.Chat.Core.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Tests.Hosting;

/// <summary>
/// A host listed in Hosting:Mounts serves the backend under a path prefix that the ingress forwards unchanged
/// (https://beta.dex.biatec.io/chat/...). The prefix moves into PathBase before tenancy, CSRF and routing; every other
/// host, and every forwarded header, is left alone.
/// </summary>
public sealed class HostMountTests : IDisposable
{
    private const string MountedHost = "beta.dex.biatec.io";
    private const string MountedOrigin = "https://beta.dex.biatec.io";

    private static Tenant MountedTenant() => new()
    {
        Id = "mount-beta-dex-biatec-io",
        Host = MountedHost,
        SignInPageUrl = MountedOrigin,
        OidcClientId = "biatec-chat",
        ClientSecretRef = "secret://biatec-chat/oidc-client-secret",
        RedirectUri = MountedOrigin + "/chat/auth/callback",
        AllowedOrigins = [MountedOrigin],
        ClientKeys = "[\"biatec-dex\"]",
        ConsentThresholdCeilingEur = 1000,
        SoftCapEurPerHour = 1500,
        TrustAnchorVerified = true,
    };

    private sealed class MemoryTenantStore(params Tenant[] tenants) : ITenantStore
    {
        public Task<Tenant?> FindByHostAsync(string host, CancellationToken ct) => Task.FromResult(tenants.FirstOrDefault(t => t.Host == host));
    }

    /// <summary>Terminal handler after the whole pipeline: reports what the request looks like once it got through.</summary>
    private sealed class ReportFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Run(ctx =>
            {
                ctx.Response.StatusCode = 200;
                return ctx.Response.WriteAsync($"{ctx.Request.PathBase}|{ctx.Request.Path}|{ctx.GetTenant()?.Id}");
            });
        };
    }

    private readonly WebApplicationFactory<Program> _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseEnvironment("Testing");
        b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Oidc:Issuer"] = "https://oidc.example.test",
            ["Oidc:McpResource"] = "https://mcp.example.test",
            ["Mcp:Endpoint"] = "https://mcp.example.test/mcp",
            ["Redis:ConnectionString"] = "127.0.0.1:1",
            ["Postgres:ConnectionString"] = "Host=127.0.0.1;Port=1;Database=x;Username=x",
            ["Algod:Networks:testnet:Url"] = "https://algod.example.test",
            ["Kms:KeyId"] = "alias/test",
            ["Hosting:Mounts:0:Host"] = MountedHost,
            ["Hosting:Mounts:0:Prefix"] = "/chat",
        }));
        b.ConfigureServices(s =>
        {
            s.RemoveAll<ITenantStore>();
            s.AddSingleton<ITenantStore>(new MemoryTenantStore(TenantSeed.BiatecChat(), MountedTenant()));
            s.AddSingleton<IStartupFilter, ReportFilter>();
        });
    });

    public void Dispose() => _factory.Dispose();

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string host, string path,
        string? origin = null, bool csrf = false, string? clientKey = null, params (string, string)[] headers)
    {
        using var client = _factory.CreateClient();
        var r = new HttpRequestMessage(method, "http://" + host + path);
        if (origin is not null) r.Headers.TryAddWithoutValidation("Origin", origin);
        if (csrf) r.Headers.TryAddWithoutValidation("X-Biatec-Chat", "1");
        if (clientKey is not null) r.Headers.TryAddWithoutValidation("X-Biatec-Client-Key", clientKey);
        foreach (var (k, v) in headers) r.Headers.TryAddWithoutValidation(k, v);
        var res = await client.SendAsync(r);
        return (res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/chat/zzz", "/chat|/zzz")]
    [InlineData("/chat/a/b?x=1", "/chat|/a/b")]
    [InlineData("/chat", "/chat|/")]
    [InlineData("/chat/", "/chat|/")]
    public async Task MountedHost_MovesThePrefixIntoPathBase_AndResolvesItsOwnTenant(string path, string expected)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, MountedHost, path);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(expected + "|mount-beta-dex-biatec-io", body);
    }

    [Fact]
    public async Task MountedHost_RoutesTheBackendsOwnChatEndpoints_BehindThePrefix()
    {
        // The backend route is POST /chat/messages, so behind the /chat mount the public URL is /chat/chat/messages.
        // Same request, same answer as on the unmounted host (here: the endpoint's own refusal, not the test's catch-all).
        var unmounted = await SendAsync(HttpMethod.Post, "chat.biatec.io", "/chat/messages", origin: "https://web.biatec.io", csrf: true, clientKey: "biatec-web");
        var mounted = await SendAsync(HttpMethod.Post, MountedHost, "/chat/chat/messages", origin: MountedOrigin, csrf: true, clientKey: "biatec-dex");

        Assert.NotEqual(HttpStatusCode.NotFound, unmounted.Status);
        Assert.DoesNotContain("|", unmounted.Body);
        Assert.Equal(unmounted.Status, mounted.Status);
        Assert.DoesNotContain("|", mounted.Body);

        // /chat/messages on the mounted host is prefix + "/messages", which is no endpoint (the test's catch-all answers).
        var prefixOnly = await SendAsync(HttpMethod.Post, MountedHost, "/chat/messages", origin: MountedOrigin, csrf: true, clientKey: "biatec-dex");
        Assert.Equal("/chat|/messages|mount-beta-dex-biatec-io", prefixOnly.Body);
    }

    [Fact]
    public async Task MountedHost_ServesHealthUnderThePrefix()
    {
        var (status, body) = await SendAsync(HttpMethod.Get, MountedHost, "/chat/health/live");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Healthy", body);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/zzz")]
    [InlineData("/health/live")]
    [InlineData("/chatty/zzz")]
    [InlineData("/chat-x")]
    [InlineData("/CHAT/zzz")]
    [InlineData("/Chat/health/live")]
    [InlineData("/auth/callback")]
    public async Task MountedHost_WithoutThePrefix_IsNotFound(string path)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, MountedHost, path);

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.DoesNotContain("|", body);
    }

    [Theory]
    [InlineData("/chat/onboarding/tenants")]
    [InlineData("/chat/onboarding/challenges")]
    [InlineData("/chat/onboarding/operator/tenants")]
    [InlineData("/chat/onboarding/operator/evidence")]
    [InlineData("/chat/ONBOARDING")]
    public async Task MountedHost_DoesNotServeTenantOnboardingOrOperatorRoutes(string path)
    {
        // A site's own origin carries the widget only; onboarding and the operator surface stay on the chat hosts.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, MountedHost, path)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, MountedHost, path)).Status);
    }

    [Theory]
    [InlineData("/chat//onboarding/tenants")]
    [InlineData("/chat/./onboarding/tenants")]
    [InlineData("/chat/%6Fnboarding/tenants")]
    [InlineData("/chat/%2e/onboarding/tenants")]
    [InlineData("/chat/x/../onboarding/tenants")]
    [InlineData("/chat//onboarding/operator/tenants")]
    [InlineData("/chat//chat/messages")]
    public async Task MountedHost_OnboardingCannotBeReachedWithOddSpellings(string path)
    {
        // With every header a real widget request carries, so only the path decides. Either blocked outright (404) or no
        // endpoint matched (the test's catch-all answers with "|"); never an endpoint.
        var (status, body) = await SendAsync(HttpMethod.Post, MountedHost, path, origin: MountedOrigin, csrf: true, clientKey: "biatec-dex");

        Assert.True(status == HttpStatusCode.NotFound || body.Contains('|'), $"{path} reached something: {(int)status} {body}");
    }

    [Theory]
    [InlineData("/chat/auth/popup")]
    [InlineData("/chat/auth/elevate/popup")]
    public async Task SignInStart_IsRefusedWith503_ForAMountedTenantWhoseRedirectUriMissesThePrefix(string path) =>
        await AssertRefusedAsync(path, MountedOrigin + "/auth/callback"); // no /chat

    [Theory]
    [InlineData("/chat/auth/popup")]
    [InlineData("/chat/auth/elevate/popup")]
    public async Task SignInStart_IsRefusedWith503_ForARedirectUriThatDiffersOnlyInCase(string path) =>
        await AssertRefusedAsync(path, MountedOrigin + "/Chat/auth/callback"); // Biatec OIDC matches the redirect URI exactly

    private async Task AssertRefusedAsync(string path, string redirectUri)
    {
        var wrong = MountedTenant();
        wrong.RedirectUri = redirectUri;
        using var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<ITenantStore>();
            s.AddSingleton<ITenantStore>(new MemoryTenantStore(wrong, TenantSeed.BiatecChat()));
        }));
        using var client = factory.CreateClient();
        var r = new HttpRequestMessage(HttpMethod.Post, $"http://{MountedHost}{path}");
        r.Headers.TryAddWithoutValidation("Origin", MountedOrigin);
        r.Headers.TryAddWithoutValidation("X-Biatec-Chat", "1");
        r.Headers.TryAddWithoutValidation("X-Biatec-Client-Key", "biatec-dex");

        var res = await client.SendAsync(r);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Contains("redirect URI", await res.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("BETA.DEX.BIATEC.IO")]
    [InlineData("beta.dex.biatec.io.")]
    public async Task MountedHost_IsMatchedLikeTheTenantHost(string hostHeader)
    {
        using var client = _factory.CreateClient();
        var r = new HttpRequestMessage(HttpMethod.Get, "http://localhost/chat/zzz");
        r.Headers.Host = hostHeader;
        var res = await client.SendAsync(r);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("/chat|/zzz|mount-beta-dex-biatec-io", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnmountedHost_IsUnchanged_EvenForPathsThatStartWithTheMountPrefix()
    {
        var (status, body) = await SendAsync(HttpMethod.Get, "chat.biatec.io", "/chat/zzz");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("|/chat/zzz|biatec-chat", body);
    }

    [Fact]
    public async Task ForwardedHeaders_AreNeverTrustedForTheMountOrTheTenant()
    {
        // A prefix header on an unmounted host does nothing.
        var (s1, b1) = await SendAsync(HttpMethod.Get, "chat.biatec.io", "/zzz",
            headers: [("X-Forwarded-Prefix", "/chat"), ("X-Forwarded-Host", MountedHost), ("X-Forwarded-Proto", "https")]);
        Assert.Equal(HttpStatusCode.OK, s1);
        Assert.Equal("|/zzz|biatec-chat", b1);

        // On the mounted host the tenant still comes from Host, and the configured prefix is the only one.
        var (s2, b2) = await SendAsync(HttpMethod.Get, MountedHost, "/chat/zzz",
            headers: [("X-Forwarded-Prefix", "/other"), ("X-Forwarded-Host", "chat.biatec.io")]);
        Assert.Equal(HttpStatusCode.OK, s2);
        Assert.Equal("/chat|/zzz|mount-beta-dex-biatec-io", b2);

        // A stripped request cannot be declared by a header: without the prefix the mounted host is a 404.
        var (s3, _) = await SendAsync(HttpMethod.Get, MountedHost, "/zzz", headers: [("X-Forwarded-Prefix", "/chat")]);
        Assert.Equal(HttpStatusCode.NotFound, s3);
    }

    [Fact]
    public async Task CsrfAndOriginGuards_SeeThePathWithoutThePrefix()
    {
        // Not public: needs the custom header, an allowed origin and a registered client key.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, MountedHost, "/chat/chat/zzz", origin: MountedOrigin)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, MountedHost, "/chat/chat/zzz", origin: "https://evil.example", csrf: true, clientKey: "biatec-dex")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, MountedHost, "/chat/chat/zzz", origin: MountedOrigin, csrf: true, clientKey: "biatec-web")).Status);
        var (ok, body) = await SendAsync(HttpMethod.Post, MountedHost, "/chat/chat/zzz", origin: MountedOrigin, csrf: true, clientKey: "biatec-dex");
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal("/chat|/chat/zzz|mount-beta-dex-biatec-io", body);

        // /public/* is exempt exactly as on an unmounted host, but only once the prefix is gone.
        var (pub, pubBody) = await SendAsync(HttpMethod.Post, MountedHost, "/chat/public/zzz");
        Assert.Equal(HttpStatusCode.OK, pub);
        Assert.Equal("/chat|/public/zzz|mount-beta-dex-biatec-io", pubBody);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public readonly List<string> Errors = [];
        public ILogger CreateLogger(string categoryName) => new Capture(this);
        public void Dispose() { }
        private sealed class Capture(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Error) lock (owner.Errors) owner.Errors.Add(formatter(state, exception));
            }
        }
    }

    [Fact]
    public async Task ATenantWhoseRedirectUriMissesTheMountPrefix_IsLoggedAsAnErrorOnce()
    {
        var logs = new CapturingLoggerProvider();
        var wrong = MountedTenant();
        wrong.RedirectUri = MountedOrigin + "/auth/callback"; // no /chat
        using var factory = _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureLogging(l => l.AddProvider(logs));
            b.ConfigureServices(s => { s.RemoveAll<ITenantStore>(); s.AddSingleton<ITenantStore>(new MemoryTenantStore(wrong, TenantSeed.BiatecChat())); });
        });
        using var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
            await client.GetAsync($"http://{MountedHost}/chat/zzz");
        await client.GetAsync("http://chat.biatec.io/zzz"); // an unmounted tenant never warns
        await client.GetAsync($"http://{MountedHost}/chat/zzz");

        var error = Assert.Single(logs.Errors, w => w.Contains("mount-beta-dex-biatec-io"));
        Assert.Contains(MountedOrigin + "/chat/auth/callback", error);
        Assert.DoesNotContain(logs.Errors, w => w.Contains("Tenant biatec-chat "));
    }

    [Fact]
    public async Task ATenantWhoseRedirectUriCarriesThePrefix_DoesNotWarn()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = _factory.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(logs)));
        using var client = factory.CreateClient();

        await client.GetAsync($"http://{MountedHost}/chat/zzz");

        Assert.DoesNotContain(logs.Errors, w => w.Contains("redirect URI"));
    }

    // ---- configuration -------------------------------------------------------------------------------------------

    private static ValidateOptionsResult Validate(params HostMountOptions[] mounts) =>
        new HostingOptionsValidator().Validate(null, new HostingOptions { Mounts = [.. mounts] });

    private static HostMountOptions Mount(string host, string prefix) => new() { Host = host, Prefix = prefix };

    [Theory]
    [InlineData("beta.dex.biatec.io", "/chat")]
    [InlineData("localhost", "/c")]
    [InlineData("a-b.example.test", "/api/chat")]
    [InlineData("example.test", "/chat_v2.1~x")]
    public void Validator_AcceptsAMountedHostAndPrefix(string host, string prefix) =>
        Assert.True(Validate(Mount(host, prefix)).Succeeded);

    [Theory]
    [InlineData("", "/chat", "Host")]
    [InlineData("https://beta.dex.biatec.io", "/chat", "Host")]
    [InlineData("beta.dex.biatec.io:443", "/chat", "Host")]
    [InlineData("beta.dex.biatec.io/chat", "/chat", "Host")]
    [InlineData("Beta.Dex.Biatec.io", "/chat", "Host")]
    [InlineData("beta.dex.biatec.io.", "/chat", "Host")]
    [InlineData("beta.dex.biatec.io", "", "Prefix")]
    [InlineData("beta.dex.biatec.io", "/", "Prefix")]
    [InlineData("beta.dex.biatec.io", "chat", "Prefix")]
    [InlineData("beta.dex.biatec.io", "/chat/", "Prefix")]
    [InlineData("beta.dex.biatec.io", "//chat", "Prefix")]
    [InlineData("beta.dex.biatec.io", "/chat?x=1", "Prefix")]
    [InlineData("beta.dex.biatec.io", "/chat#x", "Prefix")]
    [InlineData("beta.dex.biatec.io", "/a/../b", "Prefix")]
    [InlineData("beta.dex.biatec.io", "/%2e%2e", "Prefix")]
    [InlineData("beta.dex.biatec.io", "/ch at", "Prefix")]
    public void Validator_RejectsAMalformedMount_NamingTheOption(string host, string prefix, string option)
    {
        var result = Validate(Mount(host, prefix));

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.StartsWith("Hosting:Mounts:0:" + option));
    }

    [Theory]
    [InlineData("beta.dex.biatec.io", "/chat\n")]
    [InlineData("beta.dex.biatec.io", "/chat\r\n")]
    [InlineData("beta.dex.biatec.io\n", "/chat")]
    public void Validator_RejectsATrailingNewline_WhichDollarWouldLetThrough(string host, string prefix) =>
        Assert.True(Validate(Mount(host, prefix)).Failed);

    [Fact]
    public void Validator_DoesNotCareAboutTheDeployOnlyClientKey()
    {
        var options = new HostingOptions();
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hosting:Mounts:0:Host"] = "beta.dex.biatec.io",
            ["Hosting:Mounts:0:Prefix"] = "/chat",
            ["Hosting:Mounts:0:ClientKey"] = "not a valid key at all",
        }).Build().GetSection("Hosting").Bind(options);

        Assert.True(new HostingOptionsValidator().Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/Health/x")]
    [InlineData("/onboarding")]
    [InlineData("/ONBOARDING/chat")]
    public void Validator_RejectsAPrefixThatStartsWithAPathTheBackendOwns(string prefix) =>
        Assert.Contains(Validate(Mount("beta.dex.biatec.io", prefix)).Failures!, f => f.StartsWith("Hosting:Mounts:0:Prefix"));

    [Theory]
    [InlineData("/chat")]
    [InlineData("/auth")]
    [InlineData("/session")]
    [InlineData("/healthy")]
    [InlineData("/x/health")]
    public void Validator_AcceptsOtherPrefixes(string prefix) => Assert.True(Validate(Mount("beta.dex.biatec.io", prefix)).Succeeded);

    [Fact]
    public void TheDeploysBashChecks_UseTheSamePatternsAsTheBackendsStartupValidation()
    {
        // The deploy refuses a bad overlay before applying it, with these patterns written for bash ($ for \z). If they drift
        // apart the deploy accepts what the backend rejects at startup (a crash loop) or the other way round.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, ".github", "workflows", "deploy.yml"))) dir = dir.Parent;
        var workflow = File.ReadAllText(Path.Combine(dir!.FullName, ".github", "workflows", "deploy.yml"));

        Assert.Contains("[[ \"$host\" =~ " + HostingOptionsValidator.HostRegex.Replace("\\z", "$") + " ]]", workflow);
        Assert.Contains("[[ \"$prefix\" =~ " + HostingOptionsValidator.PrefixRegex.Replace("\\z", "$") + " ]]", workflow);
        Assert.Contains("(^|/)\\.\\.?(/|$)", workflow); // the "." and ".." segments the validator rejects
        Assert.Contains("[[ \"${prefix,,}\" =~ ^/(" + string.Join("|", HostingOptionsValidator.ReservedFirstSegments) + ")(/|$) ]]", workflow);
    }

    [Fact]
    public void Validator_RejectsTheSameHostTwice() =>
        Assert.Contains(Validate(Mount("a.example.test", "/x"), Mount("a.example.test", "/y")).Failures!, f => f.Contains("mounted twice"));

    [Fact]
    public void NoMounts_IsValid() => Assert.True(Validate().Succeeded);

    // ---- URLs handed to the browser ------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://chat.biatec.io/auth/callback", "", "https://chat.biatec.io")]
    [InlineData("https://beta.dex.biatec.io/chat/auth/callback", "/chat", "https://beta.dex.biatec.io/chat")]
    [InlineData("https://beta.dex.biatec.io/api/chat/auth/callback", "/api/chat", "https://beta.dex.biatec.io/api/chat")]
    [InlineData("https://chat.biatec.io:8443/auth/callback", "", "https://chat.biatec.io:8443")]
    [InlineData("http://localhost/auth/callback", "", "http://localhost")]
    // The prefix comes from the mount, never from the shape of the redirect URI: a tenant that is not mounted keeps its URLs.
    [InlineData("https://chat.biatec.io/somewhere/auth/callback", "", "https://chat.biatec.io")]
    [InlineData("https://chat.biatec.io/somewhere/else", "", "https://chat.biatec.io")]
    [InlineData("https://chat.biatec.io", "", "https://chat.biatec.io")]
    [InlineData("https://chat.biatec.io/auth/callback?x=1#y", "", "https://chat.biatec.io")]
    public void BackendUrls_IsTheRedirectUriOriginPlusTheHostsPathBase(string redirectUri, string pathBase, string expected) =>
        Assert.Equal(expected, BackendUrls.Base(new Tenant { Id = "t", RedirectUri = redirectUri }, pathBase));

    [Theory]
    [InlineData("https://beta.dex.biatec.io/chat/auth/callback", true)]
    [InlineData("https://beta.dex.biatec.io:443/chat/auth/callback", true)]
    [InlineData("https://BETA.dex.biatec.io/chat/auth/callback", true)]
    [InlineData("https://beta.dex.biatec.io/Chat/auth/callback", false)]
    [InlineData("https://beta.dex.biatec.io/auth/callback", false)]
    [InlineData("https://beta.dex.biatec.io/foo/chat/auth/callback", false)]
    [InlineData("https://beta.dex.biatec.io/chat/auth/callback?x=1", false)]
    [InlineData("https://beta.dex.biatec.io/chat/auth/callback#x", false)]
    [InlineData("https://beta.dex.biatec.io/chat/auth/callback/", false)]
    [InlineData("/chat/auth/callback", false)]
    [InlineData("", false)]
    public void MountConsistency_ComparesThePathOfTheStoredRedirectUri_NotARebuiltString(string redirectUri, bool consistent)
    {
        var tenant = new Tenant { Id = "t", RedirectUri = redirectUri };

        Assert.Equal(consistent, MountConsistency.IsConsistent(tenant, "/chat"));
        var signInBase = MountConsistency.SignInBase(tenant, "/chat");
        if (consistent) Assert.EndsWith("/chat", signInBase);
        else Assert.Null(signInBase);
    }

    [Fact]
    public void MountConsistency_IsAlwaysTrueOnAnUnmountedHost() =>
        Assert.True(MountConsistency.IsConsistent(new Tenant { Id = "t", RedirectUri = "https://chat.biatec.io/somewhere/else" }, ""));

    [Fact]
    public void ConfirmOrigin_IsTheBareOrigin_NeverAMountedUrl() =>
        Assert.Equal("https://beta.dex.biatec.io", Biatec.Chat.Api.Chat.Confirm.ConfirmService.BackendOrigin(MountedTenant()));

    [Fact]
    public void BackendUrls_DefaultsToNoPathBase() =>
        Assert.Equal("https://chat.biatec.io", BackendUrls.Base(new Tenant { Id = "t", RedirectUri = "https://chat.biatec.io/auth/callback" }));

    [Theory]
    [InlineData("")]
    [InlineData("/auth/callback")]
    [InlineData("not a url")]
    public void BackendUrls_WithoutAnAbsoluteRedirectUri_HasNoBase(string redirectUri)
    {
        var tenant = new Tenant { Id = "t", RedirectUri = redirectUri };

        Assert.Null(BackendUrls.TryBase(tenant, "/chat"));
        Assert.Throws<UriFormatException>(() => BackendUrls.Base(tenant, "/chat"));
    }

    private static readonly IReadOnlyDictionary<string, PathString> ChatMount =
        Biatec.Chat.Api.Telemetry.TelemetryRegistration.MountedHealthPaths([new() { Host = MountedHost, Prefix = "/chat" }]);

    [Theory]
    [InlineData(MountedHost, "/health", true)]
    [InlineData(MountedHost, "/health/live", true)]
    [InlineData(MountedHost, "/chat/health", true)]
    [InlineData(MountedHost, "/chat/health/live", true)]
    [InlineData(MountedHost, "/CHAT/HEALTH", false)]
    [InlineData("chat.biatec.io", "/health", true)]
    [InlineData(MountedHost, "/chat/chat/messages", false)]
    [InlineData(MountedHost, "/healthy", false)]
    [InlineData(MountedHost, "/chat/healthcare", false)]
    // Only the mount prefix of the request's own host counts, so a caller cannot hide a request from tracing by appending /health.
    [InlineData(MountedHost, "/public/health", false)]
    [InlineData(MountedHost, "/onboarding/health", false)]
    [InlineData(MountedHost, "/other/health", false)]
    [InlineData("chat.biatec.io", "/chat/health", false)]
    [InlineData(null, "/chat/health", false)]
    [InlineData(MountedHost, "/", false)]
    public void Telemetry_DoesNotTraceProbes_OnlyBehindTheMountOfTheRequestsOwnHost(string? host, string path, bool probe) =>
        Assert.Equal(probe, Biatec.Chat.Api.Telemetry.TelemetryRegistration.IsHealthRequest(path, host, ChatMount));

    [Fact]
    public void Telemetry_WithoutMounts_OnlyTheBareProbePathCounts()
    {
        var none = Biatec.Chat.Api.Telemetry.TelemetryRegistration.MountedHealthPaths([]);
        Assert.True(Biatec.Chat.Api.Telemetry.TelemetryRegistration.IsHealthRequest("/health", MountedHost, none));
        Assert.False(Biatec.Chat.Api.Telemetry.TelemetryRegistration.IsHealthRequest("/chat/health", MountedHost, none));
    }

    [Theory]
    [InlineData("", "", "https://chat.biatec.io/.well-known/evidence-jwks.json")]
    [InlineData("", "/chat", "https://chat.biatec.io/chat/.well-known/evidence-jwks.json")]
    // A configured public base wins: both URLs serve the same keys.
    [InlineData("https://stage.chat.biatec.io/", "/chat", "https://stage.chat.biatec.io/.well-known/evidence-jwks.json")]
    public void EvidenceJwksUrl_UsesTheConfiguredBase_ElseTheRequestOriginWithItsPathBase(string publicBase, string pathBase, string expected)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString("chat.biatec.io");
        ctx.Request.PathBase = pathBase;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Evidence:PublicBaseUrl"] = publicBase }).Build();

        Assert.Equal(expected, Biatec.Chat.Api.Evidence.Jwks.EvidenceJwks.PublicUrl(ctx, config));
    }

    private static ToolExecutionContext Ctx(Tenant tenant, string pathBase = "") =>
        new(CapabilityRegistry.CreateDefault().Find("requestSignIn")!, "tu-1", Guid.NewGuid(), "s", tenant.Id, Biatec.Chat.Core.Data.SessionTier.Anonymous, [], tenant, true, pathBase);

    [Fact]
    public async Task SignInCard_OfAMountedTenant_PointsAtThePrefixedPopupEndpoint()
    {
        var r = await new RequestSignInExecutor().ExecuteAsync(Ctx(MountedTenant(), "/chat"), new JsonObject(), default);

        var card = Assert.IsType<SignInCardEvent>(Assert.Single(r.Events!));
        Assert.Equal("https://beta.dex.biatec.io/chat/auth/popup", card.SignInUrl);
        Assert.False(card.Unavailable);
    }

    [Fact]
    public async Task SignInCard_OfAnUnmountedTenant_IsUnchanged()
    {
        var r = await new RequestSignInExecutor().ExecuteAsync(Ctx(TenantSeed.BiatecChat()), new JsonObject(), default);

        Assert.Equal("https://chat.biatec.io/auth/popup", Assert.IsType<SignInCardEvent>(Assert.Single(r.Events!)).SignInUrl);
    }
}
