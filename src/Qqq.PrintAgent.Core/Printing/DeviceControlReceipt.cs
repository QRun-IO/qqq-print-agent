namespace Qqq.PrintAgent.Core.Printing;

public sealed record DeviceControlReceipt(string ReceiptId, string Outcome, string CompletedAt, string? Message = null);
