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
        services.TryAddSingleton<IAmazonBedrockRuntime>(sp => CreateBedrockRuntime(sp.GetRequiredService<IOptions<BedrockOptions>>().Value));
        services.TryAddSingleton<IConverseStreamSource, BedrockConverseStreamSource>();
        services.TryAddSingleton<IModelClient, BedrockModelClient>();
        return services;
    }

    /// <summary>
    /// Adaptive mode keeps the SDK's client-side rate limiter, which slows down after throttling; the SDK's own
    /// retries are off because <see cref="BedrockModelClient"/> retries with jitter and must not retry mid-stream.
    /// </summary>
    public static IAmazonBedrockRuntime CreateBedrockRuntime(BedrockOptions o) => new AmazonBedrockRuntimeClient(new AmazonBedrockRuntimeConfig
    {
        RegionEndpoint = RegionEndpoint.GetBySystemName(o.Region),
        RetryMode = RequestRetryMode.Adaptive,
        MaxErrorRetry = 0,
    });
}
