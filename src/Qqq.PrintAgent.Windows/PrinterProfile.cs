namespace Qqq.PrintAgent.Windows;

/// <summary>Customer-facing queue metadata; the Windows queue name and printer ID remain unchanged.</summary>
public sealed record PrinterProfile(string? DisplayName = null, string? Description = null, string? Location = null);
