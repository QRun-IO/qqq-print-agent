using System.Security.Cryptography;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

public sealed partial class AgentStore
{
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
         return JsonSerializer.Deserialize<AgentAuthentication>(bytes, PrintApiClient.JsonOptions)
            ?? throw new InvalidDataException("Saved QQQ authorization is invalid.");
      }
      finally { CryptographicOperations.ZeroMemory(bytes); }
   }

   public void SaveAuthentication(AgentAuthentication authentication)
   {
      byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(authentication, PrintApiClient.JsonOptions);
      try
      {
         byte[] encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
         string path = Path.Combine(_directory, "qqq-session.dpapi");
         File.WriteAllBytes(path + ".tmp", encrypted);
         File.Move(path + ".tmp", path, overwrite: true);
      }
      finally { CryptographicOperations.ZeroMemory(bytes); }
   }
}
