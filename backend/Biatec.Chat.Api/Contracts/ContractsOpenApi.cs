namespace Biatec.Chat.Api.Contracts;

public static class ContractsOpenApi
{
    /// <summary>
    /// Registers the OpenAPI document. A schema transformer marks the <c>type</c> discriminator as required on
    /// every <see cref="ChatEvent"/> variant, so the generated TypeScript is a true discriminated union.
    /// </summary>
    public static IServiceCollection AddContractsOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(options =>
        {
            options.AddSchemaTransformer((schema, context, _) =>
            {
                var type = context.JsonTypeInfo.Type;
                if (type != typeof(ChatEvent) && typeof(ChatEvent).IsAssignableFrom(type))
                {
                    schema.Required ??= new HashSet<string>();
                    schema.Required.Add("type");
                }
                return Task.CompletedTask;
            });
        });
        return services;
    }
}
