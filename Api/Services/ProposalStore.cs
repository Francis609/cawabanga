using System.Collections.Concurrent;
using System.Text.Json;

namespace CawaBanga.Api.Services;

public sealed class ProposalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ConcurrentDictionary<Guid, EditProposal> _proposals = new();
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly string _filePath;

    public ProposalStore(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var configuredPath = configuration["ProposalStore:Path"];
        _filePath = Path.GetFullPath(configuredPath ?? Path.Combine(environment.ContentRootPath, "data", "proposals.json"));
        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            var existing = JsonSerializer.Deserialize<List<EditProposal>>(File.ReadAllText(_filePath), JsonOptions) ?? [];
            foreach (var proposal in existing)
            {
                _proposals[proposal.Id] = proposal;
            }
        }
        catch (JsonException)
        {
            throw new InvalidDataException($"Proposal store file could not be parsed: {_filePath}");
        }
    }

    public Task<IReadOnlyList<EditProposal>> GetAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EditProposal>>(_proposals.Values.OrderByDescending(item => item.CreatedAtUtc).ToArray());

    public Task<EditProposal?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(_proposals.TryGetValue(id, out var proposal) ? proposal : null);

    public async Task AddAsync(EditProposal proposal, CancellationToken cancellationToken)
    {
        _proposals[proposal.Id] = proposal;
        await PersistAsync(cancellationToken);
    }

    public async Task<EditProposal?> DecideAsync(Guid id, ProposalDecision decision, CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            if (!_proposals.TryGetValue(id, out var existing))
            {
                return null;
            }

            if (!string.Equals(existing.Status, ProposalDecision.Pending.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return existing;
            }

            var decided = existing with { Status = decision.ToString(), DecidedAtUtc = DateTimeOffset.UtcNow };
            _proposals[id] = decided;
            await WriteFileAsync(cancellationToken);
            return decided;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            await WriteFileAsync(cancellationToken);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task WriteFileAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(_proposals.Values.OrderByDescending(item => item.CreatedAtUtc), JsonOptions),
                cancellationToken);
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
