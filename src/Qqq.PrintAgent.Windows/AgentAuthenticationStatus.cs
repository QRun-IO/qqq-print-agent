namespace Qqq.PrintAgent.Windows;

/// <summary>Safe companion status, with no cookie, password or device proof.</summary>
public sealed record AgentAuthenticationStatus(bool SignedIn, string? PrincipalId, string? TenantId, string Message, bool Authorized = false);
