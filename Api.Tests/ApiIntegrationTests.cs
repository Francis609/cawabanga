using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CawaBanga.Api.Tests;

public sealed class ApiIntegrationTests
{
    [Fact]
    public async Task Health_returns_ok()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Empty_brief_is_rejected_without_calling_model()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/proposals", new { brief = " " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.ModelHandler.RequestCount);
    }

    [Fact]
    public async Task Proposal_can_be_created_listed_and_approved()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var createResponse = await client.PostAsJsonAsync("/api/proposals", new
        {
            brief = "Trim the opening and add a title",
            projectPath = Path.GetTempPath(),
            targetApplication = "resolve"
        });
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal("Pending", created.GetProperty("status").GetString());

        var proposals = await client.GetFromJsonAsync<JsonElement[]>("/api/proposals");
        Assert.NotNull(proposals);
        Assert.Contains(proposals, proposal => proposal.GetProperty("id").GetGuid() == id);

        using var decisionResponse = await client.PostAsJsonAsync($"/api/proposals/{id}/decision", new { decision = "approve" });
        var decided = await decisionResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, decisionResponse.StatusCode);
        Assert.Equal("Approved", decided.GetProperty("status").GetString());
        Assert.True(decided.GetProperty("decidedAtUtc").ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task Missing_proposal_returns_not_found()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var getResponse = await client.GetAsync($"/api/proposals/{Guid.NewGuid()}");
        using var decisionResponse = await client.PostAsJsonAsync($"/api/proposals/{Guid.NewGuid()}/decision", new { decision = "approve" });

        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, decisionResponse.StatusCode);
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Invalid_decision_is_rejected(string decision)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync($"/api/proposals/{Guid.NewGuid()}/decision", new { decision });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Decided_proposal_cannot_be_changed_to_a_second_decision()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var proposalId = await CreateProposalAsync(client);

        using var approveResponse = await client.PostAsJsonAsync($"/api/proposals/{proposalId}/decision", new { decision = "approve" });
        var approved = await approveResponse.Content.ReadFromJsonAsync<JsonElement>();
        var decidedAt = approved.GetProperty("decidedAtUtc").GetDateTimeOffset();

        using var rejectResponse = await client.PostAsJsonAsync($"/api/proposals/{proposalId}/decision", new { decision = "reject" });
        var stillApproved = await rejectResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, approveResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, rejectResponse.StatusCode);
        Assert.Equal("Approved", stillApproved.GetProperty("status").GetString());
        Assert.Equal(decidedAt, stillApproved.GetProperty("decidedAtUtc").GetDateTimeOffset());
    }

    private static async Task<Guid> CreateProposalAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/proposals", new
        {
            brief = "Trim the opening",
            projectPath = Path.GetTempPath()
        });
        response.EnsureSuccessStatusCode();
        var proposal = await response.Content.ReadFromJsonAsync<JsonElement>();
        return proposal.GetProperty("id").GetGuid();
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("{\"unexpected\":true}")]
    [InlineData("{\"response\":\"{broken\"}")]
    [InlineData("{\"response\":\"{\\\"steps\\\":[]}\"}")]
    public async Task Malformed_model_response_returns_bad_gateway_without_saving_proposal(string modelResponse)
    {
        using var factory = new ApiFactory(modelResponseContent: modelResponse);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/proposals", new
        {
            brief = "Add a title",
            projectPath = Path.GetTempPath()
        });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var proposals = await client.GetFromJsonAsync<JsonElement[]>("/api/proposals");
        Assert.NotNull(proposals);
        Assert.Empty(proposals);
    }

    [Fact]
    public async Task Proposal_and_decision_survive_store_reload()
    {
        var storePath = Path.Combine(Path.GetTempPath(), $"cawabanga-reload-{Guid.NewGuid():N}.json");
        try
        {
            Guid proposalId;
            using (var firstFactory = new ApiFactory(storePath: storePath))
            using (var firstClient = firstFactory.CreateClient())
            {
                proposalId = await CreateProposalAsync(firstClient);
                using var decisionResponse = await firstClient.PostAsJsonAsync(
                    $"/api/proposals/{proposalId}/decision", new { decision = "approve" });
                Assert.Equal(HttpStatusCode.OK, decisionResponse.StatusCode);
            }

            using var secondFactory = new ApiFactory(storePath: storePath);
            using var secondClient = secondFactory.CreateClient();
            using var getResponse = await secondClient.GetAsync($"/api/proposals/{proposalId}");
            var proposal = await getResponse.Content.ReadFromJsonAsync<JsonElement>();

            Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
            Assert.Equal(proposalId, proposal.GetProperty("id").GetGuid());
            Assert.Equal("Approved", proposal.GetProperty("status").GetString());
            Assert.True(proposal.GetProperty("decidedAtUtc").ValueKind != JsonValueKind.Null);
        }
        finally
        {
            if (File.Exists(storePath))
            {
                File.Delete(storePath);
            }
        }
    }

    [Fact]
    public async Task Ollama_unavailable_returns_service_unavailable()
    {
        using var factory = new ApiFactory(modelStatusCode: HttpStatusCode.ServiceUnavailable);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/proposals", new
        {
            brief = "Add a title",
            projectPath = Path.GetTempPath()
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private sealed class ApiFactory(
        HttpStatusCode modelStatusCode = HttpStatusCode.OK,
        string? modelResponseContent = null,
        string? storePath = null) : WebApplicationFactory<global::Program>
    {
        private readonly string _storePath = storePath ?? Path.Combine(Path.GetTempPath(), $"cawabanga-tests-{Guid.NewGuid():N}.json");
        private readonly bool _ownsStorePath = storePath is null;
        public ModelResponseHandler ModelHandler { get; } = new(modelStatusCode, modelResponseContent);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ProposalStore:Path"] = _storePath }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<CawaBanga.Api.Services.OllamaProposalService>();
                services.AddHttpClient<CawaBanga.Api.Services.OllamaProposalService>()
                    .ConfigurePrimaryHttpMessageHandler(() => ModelHandler);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && _ownsStorePath && File.Exists(_storePath))
            {
                File.Delete(_storePath);
            }
        }
    }

    public sealed class ModelResponseHandler(HttpStatusCode statusCode, string? responseContent = null) : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            var content = responseContent ?? (statusCode == HttpStatusCode.OK
                ? "{\"response\":\"{\\\"summary\\\":\\\"Review a concise edit.\\\",\\\"steps\\\":[\\\"Trim the opening\\\"]}\"}"
                : "model unavailable");
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content)
            });
        }
    }
}
