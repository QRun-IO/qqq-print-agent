/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.Cryptography;
using System.Text.Json;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

public sealed partial class AgentStore
{
   private bool _authenticationRecoveryRequired;

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

   private static bool ValidIdentity(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 1024 && !value.Any(char.IsControl);

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
