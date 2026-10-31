namespace Qqq.PrintAgent.Core.Printing;

public sealed record DeviceControlCommand(string CommandId, string RequestId, string Action, string CreatedAt, string ExpiresAt, string? JobId = null);
