namespace Qqq.PrintAgent.Windows;

public sealed record JobEventSnapshot(string EventId, string Status, string RecordedAt, string? Message, string? SpoolerJobId, bool Delivered);
