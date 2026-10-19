using DnsClient;

namespace Biatec.Chat.Api.Onboarding;

/// <summary>System-resolver TXT lookups through DnsClient.</summary>
public sealed class DnsClientTxtResolver : IDnsTxtResolver
{
    private readonly ILookupClient client;

    public DnsClientTxtResolver() : this(new LookupClient(new LookupClientOptions { UseCache = false, Timeout = TimeSpan.FromSeconds(5), Retries = 1 })) { }

    /// <summary>Lets tests supply a fake lookup client.</summary>
    public DnsClientTxtResolver(ILookupClient client) => this.client = client;

    public async Task<IReadOnlyList<string>> GetTxtRecordsAsync(string name, CancellationToken ct)
    {
        try
        {
            var answer = await client.QueryAsync(name, QueryType.TXT, cancellationToken: ct);
            // NXDOMAIN and "no data" mean the record is absent; any other error is a failed lookup, not a verdict.
            if (answer.HasError && answer.Header.ResponseCode != DnsHeaderResponseCode.NotExistentDomain)
                throw new DnsLookupException($"TXT lookup for {name} returned {answer.Header.ResponseCode}.");
            return answer.Answers.TxtRecords().Select(r => string.Concat(r.Text)).ToArray();
        }
        catch (DnsLookupException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new DnsLookupException($"TXT lookup for {name} failed.", ex);
        }
    }
}
