namespace EventWall.Api.Models;

public sealed record ReportFilters(DateTime? FromUtc, DateTime? UntilUtc, PostSource? Source, string TimeZone);
public sealed class ReportCounts
{
    public int Total { get; set; }
    public int Pending { get; set; }
    public int Live { get; set; }
    public int Rejected { get; set; }
    public int Withdrawn { get; set; }
    public int ImagePosts { get; set; }
    public int Images { get; set; }
    public int FlaggedPosts { get; set; }
    public int EverApproved { get; set; }
    public int EverRejected { get; set; }
    public int EverWithdrawn { get; set; }
    public int StaffActions { get; set; }
    public int FilterActions { get; set; }
    public int SourceActions { get; set; }
    public int RepeatStaffActions { get; set; }
    public int DecisionSamples { get; set; }
    public double? MedianDecisionSeconds { get; set; }
    public double? P95DecisionSeconds { get; set; }
    public double? OldestPendingSeconds { get; set; }
    public double? ApprovalRate => Live + Rejected == 0 ? null : (double)Live / (Live + Rejected);
}
public sealed record ReportPlatform(PostSource Source, ReportCounts Counts, int BlockedAccounts);
public sealed record ReportFinding(PostFilterReason Reason, int Count);
public sealed record ReportAge(string Label, int Count);
public sealed record ReportReviewer(string Name, int Approved, int Rejected, int Withdrawn);
public sealed class ReportTimeline
{
    public required DateTime StartUtc { get; init; }
    public required string Label { get; init; }
    public Dictionary<PostSource, int> Intake { get; } = [];
    public int Approved { get; set; }
    public int Rejected { get; set; }
    public int Withdrawn { get; set; }
    public int FilterActions { get; set; }
    public int SourceActions { get; set; }
}
public sealed record ReportOperations(bool SubmissionsOpen, int ActivePromos, int TotalPromos,
    IReadOnlyList<Services.SourceStatusDto> Sources);
public sealed record EventReportDto(DateTime GeneratedAtUtc, string EventName, ReportFilters Filters,
    IReadOnlyList<PostSource> EligibleSources, IReadOnlyList<PostSource> ExcludedSources,
    ReportCounts Counts, IReadOnlyList<ReportPlatform> Platforms, DateTime? FirstReceivedUtc,
    DateTime? LastReceivedUtc, string Granularity, IReadOnlyList<ReportTimeline> Timeline,
    IReadOnlyList<ReportFinding> Findings, IReadOnlyList<ReportAge> PendingAges,
    IReadOnlyList<ReportReviewer> Reviewers, ReportOperations Operations);
