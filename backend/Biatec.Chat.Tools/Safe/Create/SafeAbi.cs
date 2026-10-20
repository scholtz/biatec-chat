using System.Buffers.Binary;
using System.Text;
using Algorand.Algod.Model.Transactions;

namespace Biatec.Chat.Tools.Safe.Create;

/// <summary>The signer group a bootstrapGroup call encodes, decoded from the transaction's own application arguments.</summary>
public sealed record EncodedSignerGroup(string Name, ulong Threshold, ulong AdminPrivileges, ulong AllowedActions, IReadOnlyList<string> Members);

/// <summary>
/// Decodes the algo-safe ABI calls of safe creation from the unsigned transaction bytes, so what is signed is checked
/// against the request, never the sidecar's description of it. Any shape it does not recognise throws
/// <see cref="SafeBuildException"/> (fail closed, including a contract ABI change).
/// </summary>
public static class SafeAbi
{
    /// <summary>createApplication(string,uint64)void</summary>
    public static readonly byte[] CreateApplication = Convert.FromHexString("bf2e1e78");
    /// <summary>bootstrapGroup((string,uint64,uint64,uint64,uint64,uint64,uint64,uint64,uint64),(address,uint64,string)[],uint64)uint64</summary>
    public static readonly byte[] BootstrapGroup = Convert.FromHexString("14a8ffba");
    /// <summary>finalizeBootstrap()void</summary>
    public static readonly byte[] FinalizeBootstrap = Convert.FromHexString("0b243ca7");

    private const int SeedHead = 2 + 8 * 8;

    public static IReadOnlyList<byte[]> Args(Transaction tx) =>
        tx.GetType().GetProperty("ApplicationArgs")?.GetValue(tx) is IEnumerable<byte[]> a ? a.ToList() : [];

    public static bool Calls(Transaction tx, byte[] selector) => Args(tx) is { Count: > 0 } a && a[0].AsSpan().SequenceEqual(selector);

    /// <summary>The safe name a createApplication call encodes.</summary>
    public static string DeployName(Transaction tx)
    {
        var a = Args(tx);
        if (a.Count != 3 || !a[0].AsSpan().SequenceEqual(CreateApplication) || a[2].Length != 8)
            throw new SafeBuildException("The deployment is not an algo-safe createApplication call.");
        return Str(a[1], 0, whole: true);
    }

    public static EncodedSignerGroup BootstrapGroupArgs(Transaction tx)
    {
        var a = Args(tx);
        if (a.Count != 4 || !a[0].AsSpan().SequenceEqual(BootstrapGroup) || a[3].Length != 8)
            throw new SafeBuildException("The call is not an algo-safe bootstrapGroup call.");
        try
        {
            var seed = a[1];
            if (seed.Length < SeedHead) throw new SafeBuildException("The signer group seed is truncated.");
            var name = Str(seed, U16(seed, 0), whole: false);
            if (U16(seed, 0) != SeedHead) throw new SafeBuildException("The signer group seed is not canonically encoded.");
            var threshold = U64(seed, 2);
            var admin = U64(seed, 10);
            var allowed = U64(seed, 18);

            var m = a[2];
            var count = U16(m, 0);
            var members = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                var start = 2 + U16(m, 2 + 2 * i);
                if (start + 32 + 8 + 2 > m.Length) throw new SafeBuildException("A member entry is truncated.");
                members.Add(new Algorand.Address(m.AsSpan(start, 32).ToArray()).EncodeAsString());
            }
            return new EncodedSignerGroup(name, threshold, admin, allowed, members);
        }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or DecoderFallbackException)
        {
            throw new SafeBuildException("The bootstrapGroup arguments could not be decoded.");
        }
    }

    public static bool IsFinalize(Transaction tx) => Args(tx) is { Count: 1 } a && a[0].AsSpan().SequenceEqual(FinalizeBootstrap);

    private static int U16(byte[] b, int at) =>
        at + 2 <= b.Length ? BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at, 2)) : throw new SafeBuildException("An ABI value is truncated.");

    private static ulong U64(byte[] b, int at) =>
        at + 8 <= b.Length ? BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(at, 8)) : throw new SafeBuildException("An ABI value is truncated.");

    /// <summary>ABI string at <paramref name="at"/>: a uint16 length then UTF-8 bytes. <paramref name="whole"/> requires it to fill the buffer.</summary>
    private static string Str(byte[] b, int at, bool whole)
    {
        var len = U16(b, at);
        if (at + 2 + len > b.Length || (whole && at + 2 + len != b.Length)) throw new SafeBuildException("An ABI string is malformed.");
        return new UTF8Encoding(false, true).GetString(b, at + 2, len);
    }
}
