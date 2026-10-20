using System.Text.Json.Nodes;
using Biatec.Chat.Api.Chat.SignIn;
using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Api.Orchestration;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Core.Capabilities;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Tools.Kb;

namespace Biatec.Chat.Tests.Kb;

public sealed class KbSearchTests
{
    private static string SeedDir()
    {
        // Walk up from the test binary to the repo's docs/kb.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "kb"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "docs", "kb");
    }

    private static ToolExecutionContext Ctx(string name, Tenant? tenant, bool signInAvailable = true) =>
        new(CapabilityRegistry.CreateDefault().Find(name)!, "tu-1", Guid.NewGuid(), "s", "t", Biatec.Chat.Core.Data.SessionTier.Anonymous, [], tenant, signInAvailable);

    [Fact]
    public void Kb_SeedPagesLoad_AndCoverTheRequiredTopics()
    {
        var kb = KbSearchService.FromDirectory(SeedDir());
        Assert.True(kb.PageCount >= 6);
        foreach (var topic in new[] { "Biatec products", "Algorand", "DEX", "Safe", "fees", "getting started" })
            Assert.NotEmpty(kb.Search(topic));
    }

    [Fact]
    public void Kb_WhatIsBiatec_RanksTheOverviewFirst_WithCitableId()
    {
        var kb = KbSearchService.FromDirectory(SeedDir());
        var hits = kb.Search("What is Biatec?");
        Assert.Equal("what-is-biatec", hits[0].Id);
        Assert.Contains("self-custody", hits[0].Excerpt);
    }

    [Fact]
    public void Kb_NoMatchAndStopwordOnlyQueriesReturnNothing_AndMissingDirectoryIsEmpty()
    {
        var kb = KbSearchService.FromDirectory(SeedDir());
        Assert.Empty(kb.Search("zzzqqq"));
        Assert.Empty(kb.Search("what is the"));
        Assert.Equal(0, KbSearchService.FromDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())).PageCount);
    }

    [Fact]
    public void Kb_Replace_SwapsTheSnapshot_AndMergeLetsUploadsOverrideSeed()
    {
        var kb = KbSearchService.FromDirectory(SeedDir());
        var before = kb.Pages;
        var merged = KbSearchService.Merge(before, [KbSearchService.Parse("what-is-biatec", "# Overview\n\nquokka text"), KbSearchService.Parse("new-page", "no heading, wombat")]);
        kb.Replace(merged);

        Assert.Equal(before.Count + 1, kb.PageCount);
        Assert.Equal("Overview", kb.Search("quokka").Single().Title);
        Assert.Equal("new-page", kb.Search("wombat").Single().Title); // no heading: the id is the title
        Assert.DoesNotContain(before, p => p.Body.Contains("quokka")); // an old snapshot never changes
    }

    [Fact]
    public async Task Kb_ExecutorReturnsCitedContent_AndHandlesNoHits()
    {
        var exec = new KbSearchExecutor(KbSearchService.FromDirectory(SeedDir()));
        var ok = await exec.ExecuteAsync(Ctx("kb.search", null), new JsonObject { ["query"] = "what is Biatec" }, default);
        Assert.False(ok.IsError);
        Assert.Contains("[source: what-is-biatec]", ok.Content);

        var none = await exec.ExecuteAsync(Ctx("kb.search", null), new JsonObject { ["query"] = "zzzqqq" }, default);
        Assert.Contains("No knowledge base page matched", none.Content);
    }

    [Fact]
    public async Task Kb_RequestSignIn_EmitsSignInCard_ForCookieTransport()
    {
        var exec = new RequestSignInExecutor();
        var r = await exec.ExecuteAsync(Ctx("requestSignIn", TenantSeed.BiatecChat()),
            new JsonObject { ["reason"] = "Moving funds needs an account." }, default);
        var card = Assert.IsType<SignInCardEvent>(Assert.Single(r.Events!));
        Assert.Equal("https://chat.biatec.io/auth/popup", card.SignInUrl);
        Assert.Equal(SignInReason.SignIn, card.Reason);
        Assert.False(card.Unavailable);
        Assert.Equal("Moving funds needs an account.", card.Description);
    }

    [Fact]
    public async Task Kb_RequestSignIn_WithoutAbsoluteRedirectUri_FallsBackToUnavailableCard_NotRelativeUrl()
    {
        var exec = new RequestSignInExecutor();
        var tenant = TenantSeed.BiatecChat();
        tenant.RedirectUri = "";
        var r = await exec.ExecuteAsync(Ctx("requestSignIn", tenant), new JsonObject(), default);
        var card = Assert.IsType<SignInCardEvent>(Assert.Single(r.Events!));
        Assert.True(card.Unavailable);
        Assert.StartsWith("http", card.SignInUrl);
    }

    [Fact]
    public async Task Kb_RequestSignIn_OnPublicTransport_LinksToTenantSignInPage()
    {
        var exec = new RequestSignInExecutor();
        var r = await exec.ExecuteAsync(Ctx("requestSignIn", TenantSeed.BiatecChat(), signInAvailable: false), new JsonObject(), default);
        var card = Assert.IsType<SignInCardEvent>(Assert.Single(r.Events!));
        Assert.Equal("https://web.biatec.io", card.SignInUrl);
        Assert.Contains("not available", card.Description);
        Assert.True(card.Unavailable);

        var noPage = TenantSeed.BiatecChat();
        noPage.SignInPageUrl = null;
        var r2 = await exec.ExecuteAsync(Ctx("requestSignIn", noPage, signInAvailable: false), new JsonObject(), default);
        Assert.Null(r2.Events);
        Assert.Contains("unavailable", r2.Content);
    }
}
