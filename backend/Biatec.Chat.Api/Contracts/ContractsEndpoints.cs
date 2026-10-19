namespace Biatec.Chat.Api.Contracts;

/// <summary>
/// Declares the wire contracts in the OpenAPI document so the TypeScript chat-protocol package can be generated
/// from it. The endpoint only documents the types; the real endpoints replace it.
/// </summary>
public static class ContractsEndpoints
{
    public sealed record ContractCatalog
    {
        public required SessionState Session { get; init; }
        public required SuggestionList Suggestions { get; init; }
        public required ChatMessageRequest ChatRequest { get; init; }
        public required ActionDecisionRequest ActionDecision { get; init; }
        public required ChatEvent Event { get; init; }
    }

    public static IEndpointRouteBuilder MapContractCatalog(this IEndpointRouteBuilder app)
    {
        app.MapGet("/contracts", () => Results.StatusCode(StatusCodes.Status501NotImplemented))
            .WithName("GetContractCatalog")
            .Produces<ContractCatalog>();
        return app;
    }
}
