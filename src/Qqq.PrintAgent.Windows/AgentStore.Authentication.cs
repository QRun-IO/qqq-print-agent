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
   private const string SessionFile = "qqq-session.dpapi";
   private const string StagedSessionFile = "qqq-session.staged.dpapi";
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
         File.Move(Path.Combine(_directory, SessionFile), Path.Combine(_directory, "qqq-session.recovery-" + Guid.NewGuid().ToString("N") + ".dpapi"));
         _authenticationRecoveryRequired = false;
         return true;
      }
      return false;
   }



   /// <summary>
   /// Reads and decrypts the saved session, or returns null when none exists. A pairing change that was staged and not
   /// finished is finished first, so the session returned always belongs to the stored pairing. The file is size-bounded,
   /// decrypted for the current service account, parsed and validated, and the decrypted bytes are zeroed afterwards. A
   /// damaged or invalid file throws.
   /// </summary>
   public AgentAuthentication? LoadAuthentication()
   {
      try
      {
         CompleteStagedPairing();
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
      {
         // The staged session could not be moved into place now; the next reader tries again, and until then the stored session is the earlier one.
      }
      return ReadAuthentication(Path.Combine(_directory, SessionFile));
   }



   /// <summary>Reads, decrypts, parses and validates one protected session file, or returns null when it does not exist.</summary>
   private static AgentAuthentication? ReadAuthentication(string path)
   {
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
   /// leave a half-written secret. An unreadable earlier file is quarantined first. A staged session from an unfinished pairing
   /// change is removed, because what is saved here is newer than it.
   /// </summary>
   public void SaveAuthentication(AgentAuthentication authentication)
   {
      string path = Path.Combine(_directory, SessionFile);
      WriteProtected(path + ".tmp", authentication);
      _ = QuarantineUnreadableAuthentication();
      File.Move(path + ".tmp", path, overwrite: true);
      File.Delete(Path.Combine(_directory, StagedSessionFile));
   }



   /// <summary>
   /// Starts replacing the pairing and its session as one step. The session is written first under a staged name and the
   /// pairing is replaced second. From then on the change is decided: this caller, a sign-in or the next service start
   /// finishes it with <see cref="CompleteStagedPairing"/>, because the staged session names the stored pairing's device. If
   /// the pairing cannot be written the staged session is removed and nothing has changed; a staged session left by a crash
   /// before the pairing was replaced names a different device and is discarded by the next reader.
   /// </summary>
   public void StagePairing(AgentConfiguration configuration, AgentAuthentication authentication)
   {
      string staged = Path.Combine(_directory, StagedSessionFile);
      WriteProtected(staged + ".tmp", authentication);
      File.Move(staged + ".tmp", staged, overwrite: true);
      try
      {
         SaveConfiguration(configuration);
      }
      catch
      {
         File.Delete(staged);
         throw;
      }
   }



   /// <summary>
   /// Finishes a staged pairing change, or discards what is left of one that never replaced the pairing. A staged session
   /// that names the stored pairing's server and device is moved into place as the session; any other (a different device,
   /// an unreadable file, or no readable pairing) is deleted. An unreadable earlier session is quarantined, not overwritten.
   /// </summary>
   public void CompleteStagedPairing()
   {
      string staged = Path.Combine(_directory, StagedSessionFile);
      if(!File.Exists(staged))
      {
         return;
      }
      if(!StagedSessionBelongsToPairing(staged))
      {
         File.Delete(staged);
         return;
      }
      string path = Path.Combine(_directory, SessionFile);
      try
      {
         _ = ReadAuthentication(path);
      }
      catch(Exception exception) when(exception is CryptographicException or JsonException or InvalidDataException or ArgumentException)
      {
         _authenticationRecoveryRequired = true;
      }
      _ = QuarantineUnreadableAuthentication();
      File.Move(staged, path, overwrite: true);
   }



   /// <summary>True when the staged session can be read and names the same server and device as the stored pairing.</summary>
   private bool StagedSessionBelongsToPairing(string staged)
   {
      try
      {
         AgentAuthentication? session = ReadAuthentication(staged);
         AgentConfiguration? pairing = LoadConfiguration();
         return session is not null && pairing is not null
            && string.Equals(session.Binding.DeviceId, pairing.DeviceId, StringComparison.Ordinal)
            && Uri.TryCreate(pairing.ServerUrl, UriKind.Absolute, out Uri? server)
            && string.Equals(server.GetLeftPart(UriPartial.Authority), session.Binding.Origin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
      }
      catch(Exception exception) when(exception is CryptographicException or JsonException or InvalidDataException or ArgumentException or UriFormatException)
      {
         return false;
      }
   }



   /// <summary>Writes one session to a file protected for the service account, zeroing the plaintext buffer on every path.</summary>
   private static void WriteProtected(string path, AgentAuthentication authentication)
   {
      byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(authentication, PrintApiClient.JsonOptions);
      try
      {
         File.WriteAllBytes(path, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
      }
      finally { CryptographicOperations.ZeroMemory(bytes); }
   }
}
