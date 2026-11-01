/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.Cryptography;
using System.Text.Json;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Saved QQQ session part of the store. The session identity and credential are kept in one DPAPI-protected file; an
/// unreadable file is quarantined (moved aside, never deleted) so a user can sign in again without losing evidence.
/// </summary>
public sealed partial class AgentStore
{
   private bool _authenticationRecoveryRequired;



   /// <summary>Loads the saved session for explicit recovery: a file that cannot be decrypted or parsed is flagged for quarantine and reported as absent, so setup can proceed instead of failing on a damaged file.</summary>
   internal AgentAuthentication? LoadAuthenticationForRecovery()
   {
      try
      {
         return LoadAuthentication();
      }
      catch(Exception exception) when(exception is CryptographicException or JsonException or InvalidDataException or ArgumentException)
      {
         _authenticationRecoveryRequired = true;
         return null;
      }
   }



   /// <summary>Moves an unreadable saved session aside under a unique recovery name, once, and reports whether it did; the damaged file is preserved rather than deleted.</summary>
   internal bool QuarantineUnreadableAuthentication()
   {
      if(_authenticationRecoveryRequired)
      {
         File.Move(Path.Combine(_directory, "qqq-session.dpapi"), Path.Combine(_directory, "qqq-session.recovery-" + Guid.NewGuid().ToString("N") + ".dpapi"));
         _authenticationRecoveryRequired = false;
         return true;
      }
      return false;
   }



   /// <summary>
   /// Reads and decrypts the saved session, or returns null when none exists. The file is size-bounded, decrypted for the
   /// current service account, parsed and validated, and the decrypted bytes are zeroed afterwards. A damaged or invalid file throws.
   /// </summary>
   public AgentAuthentication? LoadAuthentication()
   {
      string path = Path.Combine(_directory, "qqq-session.dpapi");
      if(!File.Exists(path))
      {
         return null;
      }
      if(new FileInfo(path).Length > 65_536)
      {
         throw new InvalidDataException("Saved QQQ authorization exceeded its bound.");
      }
      byte[] bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
      try
      {
         AgentAuthentication authentication = JsonSerializer.Deserialize<AgentAuthentication>(bytes, PrintApiClient.JsonOptions)
            ?? throw new InvalidDataException("Saved QQQ authorization is invalid.");
         ValidateAuthentication(authentication);
         return authentication;
      }
      finally { CryptographicOperations.ZeroMemory(bytes); }
   }



   /// <summary>Requires a saved identity to have a plain server origin (HTTPS, or HTTP only on loopback, with no credentials, path, query or fragment), bounded identity strings, and a credential for that same origin, so a tampered file cannot point the service at another server.</summary>
   private static void ValidateAuthentication(AgentAuthentication authentication)
   {
      QqqSessionBinding? binding = authentication.Binding;
      Uri? origin = binding?.Origin;
      if(origin is null || !origin.IsAbsoluteUri || origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0
         || !string.Equals(origin.AbsolutePath, "/", StringComparison.Ordinal)
         || (!string.Equals(origin.Scheme, "https", StringComparison.Ordinal)
            && !(string.Equals(origin.Scheme, "http", StringComparison.Ordinal) && origin.IsLoopback))
         || !ValidIdentity(binding!.PrincipalId) || !ValidIdentity(binding.TenantId)
         || (binding.DeviceId is not null && !ValidIdentity(binding.DeviceId))
         || (authentication.Credential is not null && !origin.Equals(authentication.Credential.Origin)))
      {
         throw new InvalidDataException("Saved QQQ authorization has an invalid identity or origin.");
      }
   }



   /// <summary>True for a non-blank identity of at most 1024 characters with no control characters.</summary>
   private static bool ValidIdentity(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 1024 && !value.Any(char.IsControl);



   /// <summary>
   /// Saves the session protected for the service account, writing a temporary file and moving it into place so a crash cannot
   /// leave a half-written secret. An unreadable earlier file is quarantined first, and the plaintext buffer is zeroed on every path.
   /// </summary>
   public void SaveAuthentication(AgentAuthentication authentication)
   {
      byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(authentication, PrintApiClient.JsonOptions);
      try
      {
         byte[] encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
         string path = Path.Combine(_directory, "qqq-session.dpapi");
         File.WriteAllBytes(path + ".tmp", encrypted);
         QuarantineUnreadableAuthentication();
         File.Move(path + ".tmp", path, overwrite: true);
      }
      finally { CryptographicOperations.ZeroMemory(bytes); }
   }
}
