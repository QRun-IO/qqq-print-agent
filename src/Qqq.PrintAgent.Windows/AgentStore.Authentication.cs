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
/// Saved QQQ session part of the store. The session identity and credential are kept in one DPAPI-protected file. While a
/// pairing change is in progress, the session that belongs to the new pairing waits beside it under a staged name until
/// it is moved into place. An unreadable session file, stored or staged, is moved aside under a recovery name and never
/// deleted, so a user can sign in again without losing evidence.
/// </summary>
public sealed partial class AgentStore
{
   private const string SessionFile = "qqq-session.dpapi";
   private const string StagedSessionFile = "qqq-session.staged.dpapi";
   private bool _authenticationRecoveryRequired;



   /// <summary>What the last read of the session did about a staged pairing change, for the caller to log.</summary>
   internal StagedPairingOutcome StagedPairing { get; private set; }



   /// <summary>Why the staged session could not be moved into place when the outcome is <see cref="StagedPairingOutcome.Deferred"/>; otherwise null.</summary>
   internal Exception? StagedPairingFailure { get; private set; }



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
   /// Returns the saved session to act on, or null when there is none. A pairing change that was staged and not finished
   /// is dealt with first: a staged session that belongs to the stored pairing is the one returned, whether or not it
   /// could be moved into place yet, so a caller never acts on a session left over from the previous pairing. Files are
   /// size-bounded, decrypted for the current service account, parsed and validated. A damaged stored file throws.
   /// </summary>
   public AgentAuthentication? LoadAuthentication() => ResolveStagedPairing() ?? ReadAuthentication(Path.Combine(_directory, SessionFile));



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
      DeleteQuietly(Path.Combine(_directory, StagedSessionFile));
   }



   /// <summary>
   /// Starts replacing the pairing and its session as one step. The session is written first, flushed to disk, under a
   /// staged name, and the pairing is replaced second. From then on the change is decided: every reader of the session
   /// uses the staged one and moves it into place, because it names the stored pairing's server and device. If the pairing
   /// cannot be written the staged session is removed and nothing has changed; a staged session left by a crash before the
   /// pairing was replaced names a different device and is discarded by the next reader.
   /// </summary>
   internal void StagePairing(AgentConfiguration configuration, AgentAuthentication authentication)
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
         DeleteQuietly(staged);
         throw;
      }
   }



   /// <summary>Deals with a staged pairing change now, as every read of the session does, and reports what was done.</summary>
   internal StagedPairingOutcome CompleteStagedPairing()
   {
      _ = ResolveStagedPairing();
      return StagedPairing;
   }



   /// <summary>
   /// Decides what a staged session is and acts on it. One that belongs to the stored pairing, while the stored session
   /// does not, is the session of a pairing change in progress: it is moved into place when that is possible, an unreadable
   /// earlier session being moved aside first, and returned either way. One that names another server or device, or that
   /// the stored session has already superseded, is a leftover and is deleted. One that cannot be read is moved aside.
   /// </summary>
   private AgentAuthentication? ResolveStagedPairing()
   {
      string staged = Path.Combine(_directory, StagedSessionFile);
      DeleteQuietly(staged + ".tmp");
      StagedPairing = StagedPairingOutcome.None;
      StagedPairingFailure = null;
      AgentAuthentication? session = File.Exists(staged) ? ReadStagedOrSetAside(staged) : null;
      if(session is null)
      {
         return null;
      }
      string path = Path.Combine(_directory, SessionFile);
      AgentConfiguration? pairing = LoadPairingOrNull();
      AgentAuthentication? stored = ReadStoredOrFlag(path);
      if(!BelongsToPairing(session, pairing) || (stored is not null && BelongsToPairing(stored, pairing)))
      {
         // The stored session is already the right answer, so a leftover that cannot be deleted now must not fail the read.
         DeleteQuietly(staged);
         StagedPairing = StagedPairingOutcome.Discarded;
         return null;
      }
      try
      {
         bool quarantined = QuarantineUnreadableAuthentication();
         File.Move(staged, path, overwrite: true);
         StagedPairing = quarantined ? StagedPairingOutcome.CompletedAfterQuarantine : StagedPairingOutcome.Completed;
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
      {
         // Something holds one of the two files open. The staged session is still the right one to act on, and the next reader moves it.
         StagedPairing = StagedPairingOutcome.Deferred;
         StagedPairingFailure = exception;
      }
      return session;
   }



   /// <summary>Reads the staged session; one that cannot be decrypted or parsed is moved aside under a recovery name, because after the pairing was replaced it may be the only copy.</summary>
   private AgentAuthentication? ReadStagedOrSetAside(string staged)
   {
      try
      {
         return ReadAuthentication(staged);
      }
      catch(Exception exception) when(exception is CryptographicException or JsonException or InvalidDataException or ArgumentException)
      {
         try
         {
            File.Move(staged, Path.Combine(_directory, "qqq-session.recovery-" + Guid.NewGuid().ToString("N") + ".dpapi"));
            StagedPairing = StagedPairingOutcome.SetAside;
         }
         catch(Exception moveFailure) when(moveFailure is IOException or UnauthorizedAccessException)
         {
            // It stays where it is and the next reader sets it aside; the stored session is still the answer to this read.
         }
         return null;
      }
   }



   /// <summary>Reads the stored session for comparison; an unreadable one is flagged for quarantine and treated as absent.</summary>
   private AgentAuthentication? ReadStoredOrFlag(string path)
   {
      try
      {
         return ReadAuthentication(path);
      }
      catch(Exception exception) when(exception is CryptographicException or JsonException or InvalidDataException or ArgumentException)
      {
         _authenticationRecoveryRequired = true;
         return null;
      }
   }



   /// <summary>Reads the stored pairing, treating one that cannot be read as absent: a pairing change only ever leaves a readable pairing behind.</summary>
   private AgentConfiguration? LoadPairingOrNull()
   {
      try
      {
         return LoadConfiguration();
      }
      catch(Exception exception) when(exception is CryptographicException or JsonException or ArgumentException or UriFormatException)
      {
         return null;
      }
   }



   /// <summary>True when a session names the same server and device as the pairing.</summary>
   private static bool BelongsToPairing(AgentAuthentication session, AgentConfiguration? pairing)
      => pairing is not null
         && string.Equals(session.Binding.DeviceId, pairing.DeviceId, StringComparison.Ordinal)
         && Uri.TryCreate(pairing.ServerUrl, UriKind.Absolute, out Uri? server)
         && string.Equals(server.GetLeftPart(UriPartial.Authority), session.Binding.Origin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);



   /// <summary>Writes one session to a file protected for the service account and flushes it to disk, zeroing the plaintext buffer on every path.</summary>
   private static void WriteProtected(string path, AgentAuthentication authentication)
   {
      byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(authentication, PrintApiClient.JsonOptions);
      try
      {
         using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
         stream.Write(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
         stream.Flush(flushToDisk: true);
      }
      finally { CryptographicOperations.ZeroMemory(bytes); }
   }



   /// <summary>Deletes a leftover file without failing the operation that noticed it.</summary>
   private static void DeleteQuietly(string path)
   {
      try
      {
         File.Delete(path);
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
      {
         // A leftover is harmless: a staged session is examined again by the next reader, and a temporary file is overwritten by the next write.
      }
   }
}
