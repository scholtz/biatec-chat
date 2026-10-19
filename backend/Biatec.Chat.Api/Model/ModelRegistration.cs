using Amazon;
using Amazon.BedrockRuntime;
using Amazon.Runtime;
using Biatec.Chat.Api.Config;
using Biatec.Chat.Api.Model.Guardrails;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Model;

public static class ModelRegistration
{
    /// <summary>
    /// Registers the Bedrock model client. The SDK client is created lazily with the default credential chain
    /// (SSO profile locally, the task role in AWS), so the app starts without AWS credentials.
    /// </summary>
    public static IServiceCollection AddBedrockModel(this IServiceCollection services)
    {
        services.AddOptions<BedrockClientOptions>()
            .BindConfiguration(BedrockOptions.Section)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddSingleton<IAmazonBedrockRuntime>(sp => CreateBedrockRuntime(sp.GetRequiredService<IOptions<BedrockOptions>>().Value));
        services.AddBedrockGuardrails();
        services.TryAddSingleton<IConverseStreamSource, BedrockConverseStreamSource>();
        services.TryAddSingleton<IModelClient, BedrockModelClient>();
        return services;
    }

    /// <summary>
    /// The SDK's own retries are off: <see cref="BedrockModelClient"/> is the single retry layer, because it knows
    /// whether anything was already streamed and must never retry after that.
    /// </summary>
    public static IAmazonBedrockRuntime CreateBedrockRuntime(BedrockOptions o) => new AmazonBedrockRuntimeClient(new AmazonBedrockRuntimeConfig
    {
        RegionEndpoint = RegionEndpoint.GetBySystemName(o.Region),
        RetryMode = RequestRetryMode.Standard,
        MaxErrorRetry = 0,
        // Covers connecting and the response headers; kept above Bedrock:FirstEventTimeoutMs (default 3 min), which
        // bounds the same phase, so the client's own stall handling decides.
        Timeout = TimeSpan.FromMinutes(10),
    });
}
