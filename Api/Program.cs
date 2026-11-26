using CawaBanga.Api;
using CawaBanga.Api.Services;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://127.0.0.1:5207");
builder.Services.AddHttpClient<OllamaProposalService>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(10);
});
builder.Services.AddSingleton<ProposalStore>();
builder.Services.AddSingleton<ResolveConnector>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/integrations", (ResolveConnector resolve) =>
    Results.Ok(new
    {
        davinciResolve = resolve.GetStatus(),
        adobe = new { status = "not-configured", detail = "Adobe integration is planned after Resolve connectivity is verified." },
        agent = new { provider = "Ollama", configured = !string.IsNullOrWhiteSpace(app.Configuration["Ollama:BaseUrl"]) }
    }));

app.MapPost("/api/proposals", async (
    CreateProposalRequest request,
    OllamaProposalService agent,
    ProposalStore store,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Brief))
    {
        return Results.BadRequest(new { error = "A non-empty editing brief is required." });
    }

    try
    {
        var proposal = await agent.CreateProposalAsync(request, cancellationToken);
        await store.AddAsync(proposal, cancellationToken);
        return Results.Created($"/api/proposals/{proposal.Id}", proposal);
    }
    catch (HttpRequestException exception)
    {
        return Results.Problem(
            title: "The local Ollama service could not be reached.",
            detail: exception.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.Problem(
            title: "The local model request timed out.",
            statusCode: StatusCodes.Status504GatewayTimeout);
    }
});

app.MapGet("/api/proposals", async (ProposalStore store, CancellationToken cancellationToken) =>
    Results.Ok(await store.GetAllAsync(cancellationToken)));

app.MapGet("/api/proposals/{id:guid}", async (Guid id, ProposalStore store, CancellationToken cancellationToken) =>
{
    var proposal = await store.GetAsync(id, cancellationToken);
    return proposal is null ? Results.NotFound() : Results.Ok(proposal);
});

app.MapPost("/api/proposals/{id:guid}/decision", async (
    Guid id,
    ProposalDecisionRequest request,
    ProposalStore store,
    CancellationToken cancellationToken) =>
{
    var decision = request.Decision.Trim().ToLowerInvariant() switch
    {
        "approve" => ProposalDecision.Approved,
        "reject" => ProposalDecision.Rejected,
        _ => ProposalDecision.Pending
    };
    if (decision == ProposalDecision.Pending)
    {
        return Results.BadRequest(new { error = "Decision must be 'approve' or 'reject'." });
    }

    var proposal = await store.DecideAsync(id, decision, cancellationToken);
    return proposal is null ? Results.NotFound() : Results.Ok(proposal);
});

app.Run();

public partial class Program;
