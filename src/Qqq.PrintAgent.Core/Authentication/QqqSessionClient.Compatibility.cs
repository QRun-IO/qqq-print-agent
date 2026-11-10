/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>
/// Compatibility part of the session client: the same public discovery and authentication requests every sign-in begins
/// with, reported as a typed result instead of a thrown failure, so the connection wizard can tell the operator which
/// step failed and why. Nothing here sends a credential, and nothing here ends a session the client holds, whatever
/// the server answers.
/// </summary>
public sealed partial class QqqSessionClient
{
   private QqqCertificateRejection? _certificateRejection;



   /// <summary>
   /// Checks whether this origin is a server the agent can use and says how a user may sign in. The server is asked
   /// afresh each time, so a retry after the operator fixed something sees the fix. An unreachable address, a refused
   /// certificate, an answer that is not QQQ discovery, a missing or unusable printing extension and an unsupported
   /// sign-in description are each returned as problems; only the caller's own cancellation and programming errors throw.
   /// A certificate the client refused is returned with the problem at whichever of the two requests it was refused.
   /// Passing the browser callback the host will use also checks that the server lists it, so the wizard never offers a
   /// browser button that cannot work. A fully usable answer is cached exactly as <see cref="DiscoverAsync"/> would.
   /// </summary>
   public async Task<QqqServerCompatibility> CheckCompatibilityAsync(Uri? browserRedirect = null, CancellationToken cancellationToken = default)
   {
      await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      {
         _diagnostics.Info($"Checking whether {Origin.Authority} is a server this agent can use.");
         Volatile.Write(ref _certificateRejection, null);
         QqqApiDiscovery discovery;
         try
         { discovery = await FetchDiscoveryAsync(keepSession: true, cancellationToken).ConfigureAwait(false); }
         catch(Exception exception)
         {
            if(Unreadable(exception, QqqCompatibilityIssue.NotQqqServer, "discovery", cancellationToken) is not { } unreadable)
            { throw; }
            return Report(new QqqServerCompatibility([unreadable], null, QqqSignInMethods.None, RefusedCertificate(unreadable)));
         }
         List<QqqCompatibilityProblem> problems = discovery.Problems(nativeSession: false, realtime: true);
         if(problems.Count != 0)
         { return Report(new QqqServerCompatibility(problems, discovery, QqqSignInMethods.None)); }
         _discovery = discovery;
         (QqqSignInMethods methods, QqqCompatibilityProblem? signIn, Uri? provider) = await SignInMethodsAsync(discovery, browserRedirect, cancellationToken).ConfigureAwait(false);
         return Report(new QqqServerCompatibility(signIn is null ? [] : [signIn], discovery, methods, RefusedCertificate(signIn), provider));
      }
      finally { _gate.Release(); }
   }



   /// <summary>
   /// Turns a failed public request into the problem it stands for, or null for a failure that says nothing about the
   /// server and must keep propagating. The caller's cancellation is checked first and thrown from here, so it wins
   /// over whatever failure surfaced at the same moment: a wizard that was closed must see its own cancellation, not
   /// "unreachable" and not a connection error.
   /// </summary>
   private QqqCompatibilityProblem? Unreadable(Exception exception, QqqCompatibilityIssue issue, string document, CancellationToken cancellationToken)
   {
      cancellationToken.ThrowIfCancellationRequested();
      return QqqServerCompatibility.FromFailure(exception, issue, document, Volatile.Read(ref _certificateRejection));
   }



   /// <summary>The certificate this check refused, when that is what the problem reports; null for every other problem, so a certificate is never shown beside a failure it did not cause.</summary>
   private QqqCertificateRejection? RefusedCertificate(QqqCompatibilityProblem? problem)
      => problem?.Issue == QqqCompatibilityIssue.SecureConnectionFailed ? Volatile.Read(ref _certificateRejection) : null;



   /// <summary>
   /// Reads the authentication metadata of a server that passed discovery and names the sign-in methods it offers, or the
   /// problem that leaves none. Table-based servers offer a password; OAuth2 servers offer the browser when their native
   /// grant passes the same checks sign-in applies and lists the host's callback. Metadata that passed is cached, as
   /// sign-in would. The origin of the provider the description names is returned whenever the description was read,
   /// usable or not.
   /// </summary>
   private async Task<(QqqSignInMethods Methods, QqqCompatibilityProblem? Problem, Uri? ProviderOrigin)> SignInMethodsAsync(QqqApiDiscovery discovery, Uri? browserRedirect, CancellationToken cancellationToken)
   {
      QqqAuthenticationMetadata metadata;
      try
      { metadata = await FetchAuthenticationAsync(keepSession: true, cancellationToken).ConfigureAwait(false); }
      catch(Exception exception)
      {
         if(Unreadable(exception, QqqCompatibilityIssue.SignInUnavailable, "sign-in description", cancellationToken) is not { } unreadable)
         { throw; }
         return (QqqSignInMethods.None, unreadable, null);
      }
      if(string.Equals(metadata.Type, "TABLE_BASED", StringComparison.Ordinal))
      {
         _authentication = metadata;
         return (QqqSignInMethods.Password, null, null);
      }
      List<string> problems = BrowserSignInProblems(discovery, metadata, browserRedirect);
      if(problems.Count != 0)
      { return (QqqSignInMethods.None, new QqqCompatibilityProblem(QqqCompatibilityIssue.SignInUnavailable, string.Join("; ", problems)), metadata.ProviderOrigin); }
      _authentication = metadata;
      return (QqqSignInMethods.Browser, null, metadata.ProviderOrigin);
   }



   /// <summary>
   /// Lists why browser sign-in cannot be offered: an authentication type the agent does not implement, a server too old
   /// for the native session grant, a grant that fails the sign-in checks, or, when the grant is otherwise usable, a
   /// callback the server does not list. An empty list means the browser button will work.
   /// </summary>
   private List<string> BrowserSignInProblems(QqqApiDiscovery discovery, QqqAuthenticationMetadata metadata, Uri? browserRedirect)
   {
      if(!string.Equals(metadata.Type, "OAUTH2", StringComparison.Ordinal))
      { return [$"authentication type '{QqqCompatibilityProblem.Printable(metadata.Type)}' is not supported; only TABLE_BASED and OAUTH2 are"]; }
      List<string> problems = [.. discovery.Problems(nativeSession: true).Select(problem => problem.Detail), .. NativeGrantProblems(metadata)];
      if(problems.Count == 0 && browserRedirect is not null
         && !(ValidRedirect(browserRedirect) && metadata.Values!.NativeSessionLogin!.RedirectUris.Contains(browserRedirect.AbsoluteUri, StringComparer.Ordinal)))
      { problems.Add($"the server's native session grant does not list the callback {QqqCompatibilityProblem.Printable(browserRedirect.OriginalString.Split('?', 2)[0])}"); }
      return problems;
   }



   /// <summary>Writes the outcome of a compatibility check to the log, one line naming every problem, and returns the result unchanged.</summary>
   private QqqServerCompatibility Report(QqqServerCompatibility result)
   {
      _diagnostics.Info(result.IsCompatible
         ? $"Compatibility check passed: printing extension {result.Discovery?.PrintingExtensionVersion ?? "(missing)"}; sign-in by {result.SignInMethods}."
         : $"Compatibility check found {result.Problems.Count} problem(s): {string.Join("; ", result.Problems.Select(problem => problem.Issue + " (" + problem.Detail + ")"))}.");
      return result;
   }
}
