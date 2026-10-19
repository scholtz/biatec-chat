using DnsClient;

namespace Biatec.Chat.Api.Onboarding;

/// <summary>System-resolver TXT lookups through DnsClient.</summary>
public sealed class DnsClientTxtResolver(ILogger<DnsClientTxtResolver> log) : IDnsTxtResolver
{
    private readonly LookupClient client = new(new LookupClientOptions { UseCache = false, Timeout = TimeSpan.FromSeconds(5), Retries = 1 });

    public async Task<IReadOnlyList<string>> GetTxtRecordsAsync(string name, CancellationToken ct)
    {
        try
        {
            var answer = await client.QueryAsync(name, QueryType.TXT, cancellationToken: ct);
            return answer.Answers.TxtRecords().Select(r => string.Concat(r.Text)).ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "TXT lookup for {Name} failed", name); return []; } // any lookup failure means "not proven"
    }
}
