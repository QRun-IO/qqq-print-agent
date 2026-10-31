using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Windows;

/// <summary>Protected service state. ToString never includes the session secret.</summary>
public sealed class AgentAuthentication(QqqSessionBinding binding, QqqSessionCredential? credential)
{
   public QqqSessionBinding Binding { get; } = binding;
   public QqqSessionCredential? Credential { get; } = credential;
   public override string ToString() => "Protected QQQ authorization state";
}

