using System.Net.Http.Json;
using System.Text.Json;

namespace CawaBanga.Api.Services;

public sealed class OllamaProposalService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public OllamaProposalService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task<EditProposal> CreateProposalAsync(CreateProposalRequest request, CancellationToken cancellationToken)
    {
        var baseUrl = _configuration["Ollama:BaseUrl"] ?? "http://127.0.0.1:11434";
        var model = _configuration["Ollama:Model"] ?? "qwen2.5:7b";
        var target = string.Equals(request.TargetApplication, "adobe", StringComparison.OrdinalIgnoreCase)
            ? "adobe"
            : "resolve";
        var projectPath = Path.GetFullPath(string.IsNullOrWhiteSpace(request.ProjectPath)
            ? Environment.CurrentDirectory
            : request.ProjectPath);
        var prompt = $"""
            You are a cautious local video-editing assistant. Return only JSON with keys summary and steps.
            The summary must briefly describe an edit plan. Steps must be an array of specific, reversible editing suggestions.
            Never claim to have executed edits, run scripts, exported media, deleted files, or changed a project.
            Do not request credentials or include shell commands. Keep the response under 1500 characters.
            Target application: {target}
            Project path: {projectPath}
            Editing brief: {request.Brief}
            """;

        using var response = await _httpClient.PostAsJsonAsync(
            $"{baseUrl.TrimEnd('/')}/api/generate",
            new { model, prompt, stream = false, format = "json" },
            cancellationToken);
        response.EnsureSuccessStatusCode();

        JsonDocument responseJson;
        try
        {
            responseJson = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The local model returned malformed response JSON.", exception);
        }

        string? generated;
        using (responseJson)
        {
            if (responseJson.RootElement.ValueKind != JsonValueKind.Object ||
                !responseJson.RootElement.TryGetProperty("response", out var responseValue) ||
                responseValue.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("The local model response did not contain a string proposal payload.");
            }

            generated = responseValue.GetString();
        }
        if (string.IsNullOrWhiteSpace(generated))
        {
            throw new InvalidDataException("The local model returned an empty proposal.");
        }

        JsonDocument proposalJson;
        try
        {
            proposalJson = JsonDocument.Parse(generated);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The local model returned malformed proposal JSON.", exception);
        }

        using (proposalJson)
        {
            var root = proposalJson.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The local model proposal must be a JSON object.");
            }

            var summary = root.TryGetProperty("summary", out var summaryValue) && summaryValue.ValueKind == JsonValueKind.String
                ? summaryValue.GetString() ?? "Review proposed editing steps."
                : "Review proposed editing steps.";
            var steps = root.TryGetProperty("steps", out var stepsValue) && stepsValue.ValueKind == JsonValueKind.Array
                ? stepsValue.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Take(20)
                    .ToArray()
                : [];

            if (steps.Length == 0)
            {
                throw new InvalidDataException("The local model returned no usable editing steps.");
            }

            return new EditProposal(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                request.Brief,
                target,
                projectPath,
                summary,
                steps,
                ProposalDecision.Pending.ToString());
        }
    }
}
