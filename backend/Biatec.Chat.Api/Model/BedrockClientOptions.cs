using System.ComponentModel.DataAnnotations;
using Biatec.Chat.Api.Config;

namespace Biatec.Chat.Api.Model;

/// <summary>Throughput settings of the Bedrock client, bound from the same <c>Bedrock</c> section as <see cref="BedrockOptions"/>.</summary>
public sealed class BedrockClientOptions
{
    /// <summary>Process-wide cap on concurrent Bedrock calls; the account's Claude rate limit is shared by the team.</summary>
    [Range(1, 256)] public int MaxConcurrency { get; set; } = 8;

    /// <summary>How long a call waits for a free slot before failing with <see cref="ModelBusyException"/>.</summary>
    [Range(1, 600000)] public int QueueTimeoutMs { get; set; } = 30000;

    /// <summary>Retries after a throttled or transiently failed call, with exponential backoff and full jitter.</summary>
    [Range(0, 10)] public int MaxRetries { get; set; } = 4;

    [Range(1, 60000)] public int RetryBaseDelayMs { get; set; } = 500;

    [Range(1, 120000)] public int RetryMaxDelayMs { get; set; } = 20000;
}

/// <summary>Every concurrency slot stayed taken for the whole queue timeout; the caller should answer "busy, try again".</summary>
public sealed class ModelBusyException(TimeSpan waited)
    : Exception($"No Bedrock slot became free within {waited.TotalSeconds:0.#} s.");
