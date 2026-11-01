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
   public bool SupportsPrinterMedia() => Extensions is not null
      && Extensions.Count(extension => extension is not null && string.Equals(extension.Name, "printing", StringComparison.Ordinal)) == 1
      && Extensions.Any(extension => extension is not null && string.Equals(extension.Name, "printing", StringComparison.Ordinal)
         && string.Equals(extension.PathPrefix, "/qbit/printing", StringComparison.Ordinal) && SupportedVersion(extension.Version, 3, "2"));



   /// <summary>One extension the server advertises: its name, semantic version and the path prefix its routes live under.</summary>
   public sealed record Extension(string Name, string Version, string PathPrefix);



   /// <summary>
   /// Checks that the server offers what this agent needs: an API version of 1.8 or later (1.9 when native browser sign-in is
   /// required, together with that feature), the core features the agent calls, the api client channel, and exactly one printing
   /// extension at the standard path in major version 1 or 2. Anything else throws a configuration error, so an unsuitable
   /// server is refused with one clear message instead of failing later in some other way.
   /// </summary>
   internal void Validate(bool nativeSession = false)
   {
      string[] required = ["version", "metaData", "processes", "processCancel", "extensions", "widgets"];
      if(!SupportedVersion(ApiVersion, nativeSession ? 9u : 8u) || Features is null
         || required.Any(feature => !Features.Contains(feature, StringComparer.Ordinal))
         || ClientChannels is null || !ClientChannels.Contains("api", StringComparer.Ordinal)
         || Extensions is null || Extensions.Count(extension => extension is not null && string.Equals(extension.Name, "printing", StringComparison.Ordinal)) != 1)
      {
         throw Configuration();
      }
      Extension printing = Extensions.Single(extension => extension is not null && string.Equals(extension.Name, "printing", StringComparison.Ordinal));
      if(!(SupportedVersion(printing.Version, 0) || SupportedVersion(printing.Version, 0, "2")) || !string.Equals(printing.PathPrefix, "/qbit/printing", StringComparison.Ordinal)
         || (nativeSession && !Features.Contains("nativeSessionLogin", StringComparer.Ordinal)))
      {
         throw Configuration();
      }
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
