namespace Qqq.PrintAgent.Core.Transport;

public sealed record ApplicationTrafficSnapshot(long UploadedBytes, long DownloadedBytes, double UploadBytesPerSecond, double DownloadBytesPerSecond, DateTimeOffset TotalsSinceUtc);
