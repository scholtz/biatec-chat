using System.Security.Cryptography;
using System.Text;
using Biatec.Chat.Api.Vault;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Biatec.Chat.Tests.Vault;

/// <summary>Runs only when Redis is reachable (docker compose up -d redis; override with VAULT_TEST_REDIS).</summary>
public sealed class RedisFactAttribute : FactAttribute
{
    public static readonly string Conn = Environment.GetEnvironmentVariable("VAULT_TEST_REDIS") ?? "127.0.0.1:6379";

    public RedisFactAttribute()
    {
        try
        {
            using var mux = ConnectionMultiplexer.Connect(Conn + ",connectTimeout=1000,abortConnect=false");
            if (!mux.IsConnected) Skip = $"Redis not reachable at {Conn}";
        }
        catch { Skip = $"Redis not reachable at {Conn}"; }
    }
}

public sealed class KmsFactAttribute : FactAttribute
{
    public KmsFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BIATEC_TEST_KMS_KEY_ID")))
            Skip = "Set BIATEC_TEST_KMS_KEY_ID (and AWS credentials for eu-central-1) to run the KMS integration test.";
    }
}

public class VaultTests
{
    private sealed class CountingRefresher(TimeSpan delay) : ITokenRefresher
    {
        private int _calls;
        public int Calls => _calls;
        public async Task<TokenRefreshResult> RefreshAsync(string refreshToken, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _calls);
            await Task.Delay(delay, ct);
            return new TokenRefreshResult
            {
                AccessToken = $"access-{n}",
                AccessExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
                RefreshToken = $"refresh-{n}-from-{refreshToken}",
            };
        }
    }

    private static TokenVaultEntry Entry(DateTimeOffset abs, DateTimeOffset? accessExp = null) => new()
    {
        AccessToken = "SECRET-ACCESS-TOKEN",
        AccessExpiresAt = accessExp ?? DateTimeOffset.UtcNow.AddMinutes(15),
        RefreshToken = "SECRET-REFRESH-TOKEN",
        IdToken = "SECRET-ID-TOKEN",
        Scopes = ["openid", "sign"],
        AbsoluteExpiresAt = abs,
    };

    private static TokenVault NewVault(IConnectionMultiplexer mux, ITokenRefresher r, IKeyProvider? keys = null) =>
        new(mux, keys ?? new DevKeyProvider(), r, Options.Create(new VaultOptions()), NullLogger<TokenVault>.Instance);

    [Fact]
    public async Task Vault_crypto_round_trips_and_binds_to_session()
    {
        var keys = new DevKeyProvider();
        var blob = await VaultCrypto.SealAsync(keys, "s1", Encoding.UTF8.GetBytes("hello"), default);
        Assert.Equal("hello", Encoding.UTF8.GetString(await VaultCrypto.OpenAsync(keys, "s1", blob, default)));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => VaultCrypto.OpenAsync(keys, "s2", blob, default));
        blob[^1] ^= 1;
        await Assert.ThrowsAnyAsync<CryptographicException>(() => VaultCrypto.OpenAsync(keys, "s1", blob, default));
    }

    [Fact]
    public async Task Vault_seal_uses_fresh_nonce_and_data_key_each_time()
    {
        var keys = new DevKeyProvider();
        var a = await VaultCrypto.SealAsync(keys, "s", [1, 2, 3], default);
        var b = await VaultCrypto.SealAsync(keys, "s", [1, 2, 3], default);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Vault_entry_and_logs_redact_tokens()
    {
        var e = Entry(DateTimeOffset.UtcNow.AddHours(8));
        Assert.DoesNotContain("SECRET", e.ToString());
        Assert.DoesNotContain("SECRET", $"{e}");
        Assert.Equal(8, Redact.Session("any-session-id").Length);
    }

    [Fact]
    public async Task Vault_dev_provider_works_without_aws()
    {
        var keys = new DevKeyProvider(RandomNumberGenerator.GetBytes(32));
        var dk = await keys.GenerateDataKeyAsync(default);
        Assert.Equal(dk.Plaintext, await keys.UnwrapDataKeyAsync(dk.Wrapped, default));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => new DevKeyProvider().UnwrapDataKeyAsync(dk.Wrapped, default));
    }

    [RedisFact]
    public async Task Vault_stores_ciphertext_with_ttl_absolute_plus_one_hour()
    {
        using var mux = await ConnectionMultiplexer.ConnectAsync(RedisFactAttribute.Conn);
        var vault = NewVault(mux, new CountingRefresher(TimeSpan.Zero));
        var sid = "t-" + Guid.NewGuid();
        var abs = DateTimeOffset.UtcNow.AddHours(8);
        await vault.PutAsync(sid, Entry(abs));
        try
        {
            byte[] raw = (await mux.GetDatabase().StringGetAsync(TokenVault.EntryKey(sid)))!;
            var text = Encoding.UTF8.GetString(raw);
            foreach (var s in new[] { "SECRET", "access", "refresh", "openid" }) Assert.DoesNotContain(s, text);

            var ttl = (await mux.GetDatabase().KeyTimeToLiveAsync(TokenVault.EntryKey(sid)))!.Value;
            Assert.InRange(ttl.TotalSeconds, 9 * 3600 - 10, 9 * 3600);

            var back = await vault.GetAsync(sid);
            Assert.Equal("SECRET-REFRESH-TOKEN", back!.RefreshToken);
            Assert.Equal("SECRET-ID-TOKEN", back.IdToken);
        }
        finally { await vault.DeleteAsync(sid); }
    }

    [RedisFact]
    public async Task Vault_concurrent_refresh_calls_the_refresher_once_across_pods()
    {
        using var mux = await ConnectionMultiplexer.ConnectAsync(RedisFactAttribute.Conn);
        var refresher = new CountingRefresher(TimeSpan.FromMilliseconds(400));
        var keys = new DevKeyProvider();
        // Two vault instances stand in for two pods sharing Redis and the key.
        var podA = NewVault(mux, refresher, keys);
        var podB = NewVault(mux, refresher, keys);
        var sid = "t-" + Guid.NewGuid();
        var abs = DateTimeOffset.UtcNow.AddHours(8);
        await podA.PutAsync(sid, Entry(abs, accessExp: DateTimeOffset.UtcNow.AddSeconds(-5)));
        try
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => (i % 2 == 0 ? podA : podB).GetAccessTokenAsync(sid)));
            Assert.Equal(1, refresher.Calls);
            Assert.All(results, r => Assert.Equal("access-1", r));

            var stored = (await podA.GetAsync(sid))!;
            Assert.Equal("refresh-1-from-SECRET-REFRESH-TOKEN", stored.RefreshToken); // rotated
            Assert.Equal(abs.ToUnixTimeSeconds(), stored.AbsoluteExpiresAt.ToUnixTimeSeconds()); // expiry inherited
            var ttl = (await mux.GetDatabase().KeyTimeToLiveAsync(TokenVault.EntryKey(sid)))!.Value;
            Assert.InRange(ttl.TotalSeconds, 9 * 3600 - 10, 9 * 3600);
            Assert.False(await mux.GetDatabase().KeyExistsAsync("biatec:vault:lock:" + sid)); // lock released
        }
        finally { await podA.DeleteAsync(sid); }
    }

    [RedisFact]
    public async Task Vault_fresh_token_is_returned_without_refresh_and_missing_session_is_null()
    {
        using var mux = await ConnectionMultiplexer.ConnectAsync(RedisFactAttribute.Conn);
        var refresher = new CountingRefresher(TimeSpan.Zero);
        var vault = NewVault(mux, refresher);
        var sid = "t-" + Guid.NewGuid();
        await vault.PutAsync(sid, Entry(DateTimeOffset.UtcNow.AddHours(8)));
        try
        {
            Assert.Equal("SECRET-ACCESS-TOKEN", await vault.GetAccessTokenAsync(sid));
            Assert.Equal(0, refresher.Calls);
            Assert.Null(await vault.GetAccessTokenAsync("t-" + Guid.NewGuid()));
        }
        finally { await vault.DeleteAsync(sid); }
    }

    [RedisFact]
    public async Task Vault_refresh_failure_does_not_leak_tokens_and_fails_waiters_fast()
    {
        using var mux = await ConnectionMultiplexer.ConnectAsync(RedisFactAttribute.Conn);
        var vault = NewVault(mux, new ThrowingRefresher());
        var sid = "t-" + Guid.NewGuid();
        await vault.PutAsync(sid, Entry(DateTimeOffset.UtcNow.AddHours(8), DateTimeOffset.UtcNow.AddSeconds(-1)));
        try
        {
            var ex = await Assert.ThrowsAsync<VaultRefreshException>(() => vault.GetAccessTokenAsync(sid));
            Assert.DoesNotContain("SECRET", ex.ToString());
            await Assert.ThrowsAsync<VaultRefreshException>(() => vault.GetAccessTokenAsync(sid)); // marker, no second grant
        }
        finally { await vault.DeleteAsync(sid); await mux.GetDatabase().KeyDeleteAsync("biatec:vault:refreshfail:" + sid); }
    }

    private sealed class ThrowingRefresher : ITokenRefresher
    {
        public Task<TokenRefreshResult> RefreshAsync(string refreshToken, CancellationToken ct) =>
            throw new HttpRequestException("boom");
    }

    [KmsFact]
    public async Task Vault_kms_provider_round_trips_data_key()
    {
        var keyId = Environment.GetEnvironmentVariable("BIATEC_TEST_KMS_KEY_ID")!;
        var kms = new KmsKeyProvider(new Amazon.KeyManagementService.AmazonKeyManagementServiceClient(Amazon.RegionEndpoint.EUCentral1), keyId);
        var blob = await VaultCrypto.SealAsync(kms, "kms-session", Encoding.UTF8.GetBytes("payload"), default);
        Assert.Equal("payload", Encoding.UTF8.GetString(await VaultCrypto.OpenAsync(kms, "kms-session", blob, default)));
    }
}
