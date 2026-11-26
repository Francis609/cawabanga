namespace CawaBanga.Api;

public sealed record CreateProposalRequest(string Brief, string? ProjectPath = null, string? TargetApplication = "resolve");

public sealed record ProposalDecisionRequest(string Decision);

public sealed record EditProposal(
    Guid Id,
    DateTimeOffset CreatedAtUtc,
    string Brief,
    string TargetApplication,
    string ProjectPath,
    string Summary,
    IReadOnlyList<string> ProposedSteps,
    string Status,
    string? DecisionNote = null,
    DateTimeOffset? DecidedAtUtc = null);

public enum ProposalDecision
{
    Pending,
    Approved,
    Rejected
}
