using Biatec.Chat.Api.Config;
using Biatec.Chat.Api.Consent;
using Biatec.Chat.Api.Vault;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Tools.Caps;
using Biatec.Chat.Tools.Caps.NoDevice;
using Biatec.Chat.Tools.Consent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Tests.Caps.NoDevice;

/// <summary>T-240: the user's own threshold from <c>GET /consent/settings</c> is used as it is (no clamp to 1000); the tenant default only replaces a missing endpoint.</summary>
public sealed class UserThresholdTests
{
    private sealed class FakeSettings(ConsentSettings? settings, bool missing = false) : IConsentSettingsClient
    {
        public Task<ConsentSettings?> GetAsync(Uri settingsEndpoint, string bearer, CancellationToken ct = default) => Task.FromResult(settings);
        public Task<ConsentSettingsFetch> FetchAsync(Uri settingsEndpoint, string bearer, CancellationToken ct = default) => Task.FromResult(new ConsentSettingsFetch(settings, missing));
    }

    private sealed class OneTokenVault : ITokenVault
    {
        public Task PutAsync(string id, TokenVaultEntry e, CancellationToken ct = default) => Task.CompletedTask;
        public Task<TokenVaultEntry?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult<TokenVaultEntry?>(null);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetAccessTokenAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>("ACCESS");
    }

    private static ConsentSettings Settings(decimal? threshold) => new(threshold, DeviceEnrolled: true, DeviceActive: true, AlwaysAsk: false, CoolingOff: false, FirstUse: false);

    private static ConsentPredictor Predictor(ConsentSettings? settings, bool missing = false) =>
        new(new OneTokenVault(), new FakeSettings(settings, missing), Options.Create(new OidcOptions { Issuer = "https://oidc.example.test", McpResource = "https://mcp.example.test" }), NullLogger<ConsentPredictor>.Instance);

    private static ChatSession Session() => new() { Id = Guid.NewGuid() };

    private static async Task<NoDeviceStatus> GateStatusAsync(ConsentSettings s)
    {
        var gate = new NoDeviceGate(new FakeSettings(s with { DeviceActive = false }), () => new Uri("https://oidc.example.test/consent/settings"),
            new NoDeviceCapOptions(), new NoDeviceSettingsCache(TimeProvider.System));
        return (await gate.CheckAsync("session", "ACCESS"))!;
    }

    [Fact]
    public async Task ThresholdAboveTheDefault_IsNotClamped_AndPredictsNoPhoneForA2000EurTransfer()
    {
        Assert.Equal(5000m, (await GateStatusAsync(Settings(5000m))).ThresholdEur);
        Assert.False(NoDevicePolicy.NeedsPhone(await GateStatusAsync(Settings(5000m)), 2000m, consentForced: false));
        Assert.Equal(ConsentPrediction.NotExpectedText, await Predictor(Settings(5000m)).PredictAsync(Session(), 2000m, CancellationToken.None));
        // Above the user's own threshold it does ask.
        Assert.True(NoDevicePolicy.NeedsPhone(await GateStatusAsync(Settings(5000m)), 5000.01m, consentForced: false));
        Assert.Equal(ConsentPrediction.AskedText, await Predictor(Settings(5000m)).PredictAsync(Session(), 5000.01m, CancellationToken.None));
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(0)]
    [InlineData(2000)]
    public async Task ThresholdZero_PredictsThePhoneForEveryAction(double value)
    {
        var v = (decimal)value;
        Assert.True(NoDevicePolicy.NeedsPhone(await GateStatusAsync(Settings(0m)), v, consentForced: false));
        Assert.Equal(ConsentPrediction.AskedText, await Predictor(Settings(0m)).PredictAsync(Session(), v, CancellationToken.None));
    }

    [Fact]
    public async Task ASettingsAnswerWithoutAThreshold_UsesTheDefault()
    {
        Assert.Equal(CapDefaults.ConsentThresholdCeilingEur, (await GateStatusAsync(Settings(null))).ThresholdEur);
    }

    [Fact]
    public async Task A404FromTheSettingsEndpoint_FallsBackToTheTenantDefaultThreshold()
    {
        var p = Predictor(null, missing: true);
        Assert.Equal(ConsentPrediction.NotExpectedText, await p.PredictAsync(Session(), 800m, CancellationToken.None, defaultThresholdEur: 1000m));
        Assert.Equal(ConsentPrediction.AskedText, await p.PredictAsync(Session(), 1200m, CancellationToken.None, defaultThresholdEur: 1000m));
        Assert.Equal(ConsentPrediction.AskedText, await p.PredictAsync(Session(), 1200m, CancellationToken.None, defaultThresholdEur: 100m));
        Assert.Equal(ConsentPrediction.NotExpectedText, await p.PredictAsync(Session(), 1200m, CancellationToken.None, defaultThresholdEur: 2500m));
        // No value, or a failure that is not a 404, keeps the cautious text.
        Assert.Equal(ConsentPrediction.FallbackText, await p.PredictAsync(Session(), null, CancellationToken.None, defaultThresholdEur: 1000m));
        Assert.Equal(ConsentPrediction.FallbackText, await Predictor(null, missing: false).PredictAsync(Session(), 10m, CancellationToken.None, defaultThresholdEur: 1000m));
    }
}
