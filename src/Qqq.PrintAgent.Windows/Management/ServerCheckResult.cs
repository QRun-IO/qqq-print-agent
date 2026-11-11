/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// What the service found when it checked a server: the five stages in order, the sign-in methods on offer, the address of
/// the sign-in provider the server names (whenever the service could read it, so the certificate warning can name both
/// addresses the exception would cover), the certificate that was refused when that is why the check failed, and the
/// identifier of the server this address already is on this computer, if it is one. It is the result of
/// <see cref="ManagementOperations.ServersCheck"/> when every stage passed, and the details of
/// <see cref="ManagementErrorCode.ServerIncompatible"/> and <see cref="ManagementErrorCode.CertificateUntrusted"/> when one
/// did not, so a client shows the same stages either way. A client ignores a sign-in method it does not know.
/// </summary>
public sealed record ServerCheckResult(IReadOnlyList<CompatibilityStageResult> Stages, IReadOnlyList<string> SignInMethods, string? ProviderOrigin = null,
   PresentedCertificate? Certificate = null, string? ExistingServerId = null)
{
   /// <summary>Sign-in method: a user name and password sent to the server's own session endpoint.</summary>
   public const string Password = "password";



   /// <summary>Sign-in method: the system browser and the server's identity provider.</summary>
   public const string Browser = "browser";



   /// <summary>
   /// Turns Core's compatibility finding into the form the pipe carries. A stage with a problem failed; a stage after
   /// the first failure that has no problem of its own was not reached; every other stage passed. The problem text is
   /// taken as Core produced it. The provider's address and the existing server are the service's own knowledge and
   /// are passed in; neither is ever taken from a client. The address and time that go with a refused certificate are
   /// the ones Core recorded with the refusal, so they cannot be passed in at all.
   /// </summary>
   public static ServerCheckResult From(QqqServerCompatibility compatibility, string? providerOrigin = null, string? existingServerId = null)
   {
      ArgumentNullException.ThrowIfNull(compatibility);
      QqqCompatibilityStage[] order = [QqqCompatibilityStage.Connection, QqqCompatibilityStage.Security, QqqCompatibilityStage.Server, QqqCompatibilityStage.Printing, QqqCompatibilityStage.SignIn];
      List<CompatibilityStageResult> stages = [];
      bool failedEarlier = false;
      for(int index = 0; index < order.Length; index++)
      {
         string[] problems = [.. compatibility.Problems.Where(problem => problem.Stage == order[index]).Select(problem => problem.Detail)];
         string outcome = problems.Length > 0 ? CompatibilityStageResult.Failed : failedEarlier ? CompatibilityStageResult.NotReached : CompatibilityStageResult.Passed;
         stages.Add(new CompatibilityStageResult(CompatibilityStageResult.Order[index], outcome, problems));
         failedEarlier |= problems.Length > 0;
      }
      List<string> methods = [];
      if(compatibility.SignInMethods.HasFlag(QqqSignInMethods.Password))
      {
         methods.Add(Password);
      }
      if(compatibility.SignInMethods.HasFlag(QqqSignInMethods.Browser))
      {
         methods.Add(Browser);
      }
      PresentedCertificate? certificate = compatibility.Certificate is null ? null : PresentedCertificate.From(compatibility.Certificate);
      return new ServerCheckResult(stages, methods, providerOrigin, certificate, existingServerId);
   }



   /// <summary>
   /// The error code this finding is answered with, or null when every stage passed and it is a success. A failed secure
   /// connection with a refused certificate is <see cref="ManagementErrorCode.CertificateUntrusted"/>, because the operator
   /// can do something about that one; any other failure is <see cref="ManagementErrorCode.ServerIncompatible"/>.
   /// </summary>
   public string? FailureCode()
   {
      if(Stages.All(stage => string.Equals(stage.Outcome, CompatibilityStageResult.Passed, StringComparison.Ordinal)))
      {
         return null;
      }
      bool securityFailed = Stages.Any(stage => string.Equals(stage.Stage, CompatibilityStageResult.Security, StringComparison.Ordinal)
         && string.Equals(stage.Outcome, CompatibilityStageResult.Failed, StringComparison.Ordinal));
      return securityFailed && Certificate is not null ? ManagementErrorCode.CertificateUntrusted : ManagementErrorCode.ServerIncompatible;
   }
}
