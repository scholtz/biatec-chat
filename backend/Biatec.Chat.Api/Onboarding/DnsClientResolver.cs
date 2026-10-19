using DnsClient;

namespace Biatec.Chat.Api.Onboarding;

/// <summary>System-resolver TXT lookups through DnsClient.</summary>
public sealed class DnsClientTxtResolver : IDnsTxtResolver
{
    private readonly LookupClient client = new(new LookupClientOptions { UseCache = false, Timeout = TimeSpan.FromSeconds(5), Retries = 1 });

    public async Task<IReadOnlyList<string>> GetTxtRecordsAsync(string name, CancellationToken ct)
    {
        try
        {
            var answer = await client.QueryAsync(name, QueryType.TXT, cancellationToken: ct);
            return answer.Answers.TxtRecords().SelectMany(r => r.Text).ToArray();
        }
        catch (DnsResponseException) { return []; }
    }
}
