namespace Qqq.PrintAgent.Windows;

public sealed record JobPage(IReadOnlyList<JobSnapshot> Jobs, int TotalCount, int Offset, int PageSize, bool ServerQueueLimited = false);
