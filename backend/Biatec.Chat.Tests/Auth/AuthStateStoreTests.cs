using Biatec.Chat.Api.Auth;
using Biatec.Chat.Api.Vault;
using Biatec.Chat.Tests.Vault;
using StackExchange.Redis;

namespace Biatec.Chat.Tests.Auth;

public class AuthStateStoreTests
{
    [RedisFact]
    public async Task RedisStore_RoundTripsEncryptedAndTakesOnce()
    {
        using var mux = await ConnectionMultiplexer.ConnectAsync(RedisFactAttribute.Conn);
        var store = new RedisAuthStateStore(mux, new DevKeyProvider(null));
        var hash = "t-" + Guid.NewGuid().ToString("N");
        await store.PutAsync(hash, new PendingAuth { SessionId = "s", TenantId = "t", PopupId = Guid.NewGuid(), Purpose = "login", Nonce = "n", Verifier = "VERIFIER-SECRET", Scope = "openid" }, TimeSpan.FromMinutes(1), default);

        byte[]? raw = await mux.GetDatabase().StringGetAsync($"biatec:auth:state:{hash}");
        Assert.DoesNotContain("VERIFIER-SECRET", System.Text.Encoding.UTF8.GetString(raw!));
        Assert.Equal("VERIFIER-SECRET", (await store.GetAsync(hash, default))!.Verifier);
        Assert.True(await store.TakeAsync(hash, default));
        Assert.False(await store.TakeAsync(hash, default));
        Assert.Null(await store.GetAsync(hash, default));
    }
}
