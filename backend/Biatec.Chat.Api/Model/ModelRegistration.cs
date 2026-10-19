using Amazon;
using Amazon.BedrockRuntime;
using Amazon.Runtime;
using Biatec.Chat.Api.Config;
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
        // A long answer (up to Bedrock:MaxTokens) streams for minutes; the SDK default would cut it off mid-answer.
        Timeout = TimeSpan.FromMinutes(10),
    });
}
