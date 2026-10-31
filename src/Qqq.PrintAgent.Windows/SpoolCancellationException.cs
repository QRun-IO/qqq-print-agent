namespace Qqq.PrintAgent.Windows;

/// <summary>A verified refusal that must survive the isolated worker boundary.</summary>
public sealed class SpoolCancellationException(string outcome, string message) : InvalidOperationException(message)
{
   public string Outcome { get; } = outcome;
}
