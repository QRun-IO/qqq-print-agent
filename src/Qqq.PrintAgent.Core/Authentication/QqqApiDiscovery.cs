/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>The existing semantic UI API discovery document, including the printing extension.</summary>
public sealed class QqqApiDiscovery
{
   public string ApiVersion { get; init; } = "";
   public IReadOnlyList<string> Features { get; init; } = [];
   public IReadOnlyList<string> ClientChannels { get; init; } = [];
   public IReadOnlyList<Extension> Extensions { get; init; } = [];



   /// <summary>
   /// Enables additive media negotiation only for one standard-path stable printing 2.3-or-newer extension within major two. Printing 2.2 is
   /// the rendering extension and carries no media negotiation, so a 2.2 server must keep this client a three-capability peer.
   /// </summary>
   public bool SupportsPrinterMedia() => HasStandardPrinting(minimumMinor: 3);



   /// <summary>
   /// Enables the printer-approval-v1 capability (the server's PRINTERS frame) only for one standard-path stable printing 2.4-or-newer
   /// extension within major two. Older servers keep the client on the operator catalog for approval, which is the 2.3 behaviour.
   /// </summary>
   public bool SupportsPrinterApproval() => HasStandardPrinting(minimumMinor: 4);



   /// <summary>True when exactly one printing extension is advertised, at the standard path, as a plain 2.x release at or above the given minor.</summary>
   private bool HasStandardPrinting(uint minimumMinor) => Extensions is not null
      && Extensions.Count(extension => extension is not null && string.Equals(extension.Name, "printing", StringComparison.Ordinal)) == 1
      && Extensions.Any(extension => extension is not null && string.Equals(extension.Name, "printing", StringComparison.Ordinal)
         && string.Equals(extension.PathPrefix, "/qbit/printing", StringComparison.Ordinal) && SupportedVersion(extension.Version, minimumMinor, "2"));



   /// <summary>The UI API capability that says the server validates the native sign-in nonce at the code exchange.</summary>
   public const string NonceValidationFeature = "nativeSessionNonceValidation";



   /// <summary>The UI API capability that says logout can return the identity provider's end-session data.</summary>
   public const string NativeSessionLogoutFeature = "nativeSessionLogout";



   /// <summary>True when the server is a plain 1.10+ release that advertises provider sign-out. The capability is additive and never inferred from native sign-in support.</summary>
   public bool SupportsNativeSessionLogout => Features?.Contains(NativeSessionLogoutFeature, StringComparer.Ordinal) == true && SupportedVersion(ApiVersion, 10);



   /// <summary>True when the server advertises that it validates the native sign-in nonce at exchange.</summary>
   public bool ValidatesNativeSessionNonce => Features?.Contains(NonceValidationFeature, StringComparer.Ordinal) == true;



   /// <summary>One extension the server advertises: its name, semantic version and the path prefix its routes live under.</summary>
   public sealed record Extension(string Name, string Version, string PathPrefix);



   /// <summary>A one-line, credential-free summary for the log: API version and every advertised extension with its version and path.</summary>
   public string Describe()
   {
      string extensions = Extensions is null || Extensions.Count == 0 ? "no extensions"
         : string.Join(", ", Extensions.Where(extension => extension is not null).Select(extension => $"{extension.Name} {extension.Version} at {extension.PathPrefix}"));
      return $"QQQ API {(string.IsNullOrEmpty(ApiVersion) ? "(missing)" : ApiVersion)}, {Features?.Count ?? 0} feature(s), client channels [{string.Join(", ", ClientChannels ?? [])}], {extensions}.";
   }



   /// <summary>
   /// Checks that the server offers what this agent needs: an API version of 1.8 or later (1.9 when native browser sign-in is
   /// required, together with that feature), the core features the agent calls, the api client channel, and exactly one printing
   /// extension at the standard path in major version 1 or 2. Anything else throws a configuration error, so an unsuitable
   /// server is refused with one clear message instead of failing later in some other way.
   /// </summary>
   internal void Validate(bool nativeSession = false, Action<string>? report = null)
   {
      List<string> problems = Problems(nativeSession);
      if(problems.Count != 0)
      {
         report?.Invoke("Discovery rejected: " + string.Join("; ", problems) + ".");
         throw Configuration();
      }
   }



   /// <summary>
   /// Lists every way the discovery document falls short of what this agent needs, in plain words, so the log can say which
   /// server-side requirement is missing. The thrown failure stays generic; only the local log sees this detail.
   /// </summary>
   private List<string> Problems(bool nativeSession)
   {
      var problems = new List<string>();
      string[] required = ["version", "metaData", "processes", "processCancel", "extensions", "widgets"];
      uint minimumMinor = nativeSession ? 9u : 8u;
      if(!SupportedVersion(ApiVersion, minimumMinor))
      { problems.Add($"API version '{ApiVersion}' is not a plain 1.{minimumMinor}+ release{(nativeSession ? " (1.9 is needed for native browser sign-in)" : "")}"); }
      if(Features is null)
      { problems.Add("no features list"); }
      else
      {
         string[] missing = [.. required.Where(feature => !Features.Contains(feature, StringComparer.Ordinal))];
         if(missing.Length != 0)
         { problems.Add("missing feature(s) " + string.Join(", ", missing)); }
         if(nativeSession && !Features.Contains("nativeSessionLogin", StringComparer.Ordinal))
         { problems.Add("the nativeSessionLogin feature is not advertised, so browser sign-in cannot be used"); }
      }
      if(ClientChannels is null || !ClientChannels.Contains("api", StringComparer.Ordinal))
      { problems.Add("the 'api' client channel is not offered"); }
      int printingCount = Extensions?.Count(extension => extension is not null && string.Equals(extension.Name, "printing", StringComparison.Ordinal)) ?? 0;
      if(printingCount != 1)
      { problems.Add(printingCount == 0 ? "no printing extension is advertised (is the printing QBit installed and enabled?)" : $"{printingCount} printing extensions are advertised, expected exactly one"); }
      else
      {
         Extension printing = Extensions!.Single(extension => extension is not null && string.Equals(extension.Name, "printing", StringComparison.Ordinal));
         if(!(SupportedVersion(printing.Version, 0) || SupportedVersion(printing.Version, 0, "2")))
         { problems.Add($"printing extension version '{printing.Version}' is not a plain 1.x or 2.x release"); }
         if(!string.Equals(printing.PathPrefix, "/qbit/printing", StringComparison.Ordinal))
         { problems.Add($"printing extension path '{printing.PathPrefix}' is not /qbit/printing"); }
      }
      return problems;
   }



   /// <summary>Accepts only a plain MAJOR.MINOR.PATCH version (digits only, no leading zeros, no pre-release suffix) in the given major with at least the given minor, so an ambiguous or pre-release string never enables a capability.</summary>
   private static bool SupportedVersion(string? value, uint minimumMinor, string major = "1")
   {
      string[] parts = value?.Split('.') ?? [];
      return parts.Length == 3 && parts.All(part => part.Length != 0 && (part.Length == 1 || part[0] != '0')
         && uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _))
         && string.Equals(parts[0], major, StringComparison.Ordinal)
         && uint.Parse(parts[1], CultureInfo.InvariantCulture) >= minimumMinor;
   }



   /// <summary>The single failure used for an unsupported or misconfigured server; its message is fixed and safe to show, and it reveals nothing about what the server sent.</summary>
   internal static QqqSessionException Configuration() => new(QqqSessionException.FailureKind.ConfigurationRequired,
      "This server does not advertise the required QQQ printing and sign-in capabilities.");
}
