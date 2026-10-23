using System.Net;
using Biatec.Chat.Api.Public;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Tests.Public;

/// <summary>BCHAT-005: the client address behind an ingress is the forwarded one only when the peer is a configured proxy.</summary>
public sealed class ClientAddressTests
{
    private static async Task<IPAddress?> ResolveAsync(Dictionary<string, string?> config, string peer, string? forwardedFor) =>
        (await ResolveWithSchemeAsync(config, peer, forwardedFor, null)).Address;

    private static async Task<(IPAddress? Address, string Scheme)> ResolveWithSchemeAsync(Dictionary<string, string?> config, string peer, string? forwardedFor, string? forwardedProto)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        ClientAddress.AddClientAddress(services, cfg);
        var options = services.BuildServiceProvider().GetRequiredService<IOptions<ForwardedHeadersOptions>>();

        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        if (forwardedFor is not null) ctx.Request.Headers["X-Forwarded-For"] = forwardedFor;
        if (forwardedProto is not null) ctx.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
        ctx.Request.Scheme = "http";
        // The same pipeline the app builds: UseClientAddress.
        var builder = new Microsoft.AspNetCore.Builder.ApplicationBuilder(new ServiceCollection().AddSingleton(options).AddLogging().BuildServiceProvider());
        ClientAddress.UseClientAddress(builder);
        await builder.Build()(ctx);
        return (ctx.Connection.RemoteIpAddress, ctx.Request.Scheme);
    }

    private static readonly Dictionary<string, string?> Overlay = new()
    {
        ["ForwardedHeaders:KnownNetworks:0"] = "10.0.0.0/8",
        ["ForwardedHeaders:KnownNetworks:1"] = "172.16.0.0/12",
        ["ForwardedHeaders:KnownNetworks:2"] = "192.168.0.0/16",
    };

    [Fact]
    public async Task Forwarded_for_from_a_configured_ingress_network_is_the_client_address() =>
        Assert.Equal(IPAddress.Parse("203.0.113.9"), await ResolveAsync(Overlay, "10.42.1.7", "203.0.113.9"));

    [Fact]
    public async Task Forwarded_for_from_a_peer_outside_the_configured_networks_is_ignored() =>
        Assert.Equal(IPAddress.Parse("198.51.100.4"), await ResolveAsync(Overlay, "198.51.100.4", "203.0.113.9"));

    [Fact]
    public async Task A_dual_stack_ipv4_mapped_peer_is_matched_against_the_ipv4_networks() =>
        Assert.Equal(IPAddress.Parse("203.0.113.9"), await ResolveAsync(Overlay, "::ffff:10.42.1.7", "203.0.113.9"));

    [Fact]
    public async Task Forwarded_proto_from_a_trusted_ingress_makes_the_request_https() =>
        Assert.Equal("https", (await ResolveWithSchemeAsync(Overlay, "10.42.1.7", "203.0.113.9", "https")).Scheme);

    [Fact]
    public async Task Forwarded_proto_from_an_untrusted_peer_is_ignored() =>
        Assert.Equal("http", (await ResolveWithSchemeAsync(Overlay, "198.51.100.4", "203.0.113.9", "https")).Scheme);

    [Fact]
    public async Task Forwarded_proto_alone_from_a_trusted_ingress_is_honoured() =>
        Assert.Equal("https", (await ResolveWithSchemeAsync(Overlay, "10.42.1.7", null, "https")).Scheme);

    [Fact]
    public async Task A_forwarded_proto_that_is_not_a_scheme_is_never_applied() =>
        Assert.Equal("http", (await ResolveWithSchemeAsync(Overlay, "10.42.1.7", "203.0.113.9", "ftp")).Scheme);

    [Fact]
    public async Task Without_configuration_forwarded_proto_is_ignored_too() =>
        Assert.Equal("http", (await ResolveWithSchemeAsync([], "10.42.1.7", "203.0.113.9", "https")).Scheme);

    [Fact]
    public async Task Without_configuration_only_loopback_is_trusted() =>
        Assert.Equal(IPAddress.Parse("10.42.1.7"), await ResolveAsync([], "10.42.1.7", "203.0.113.9"));

    [Fact]
    public async Task A_client_supplied_chain_yields_only_the_last_hop() =>
        // ForwardLimit stays 1: ingress appends the real peer, so the spoofed leading entry is never used.
        Assert.Equal(IPAddress.Parse("203.0.113.9"), await ResolveAsync(Overlay, "10.42.1.7", "1.2.3.4, 203.0.113.9"));
}
