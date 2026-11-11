/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// Arguments of <see cref="ManagementOperations.ServersRename"/>: the server's new name. Its address cannot be changed; a
/// different address is a different server. The name must be one a server may have (<see cref="ServerName"/>): the
/// record cannot be built with another, in code or from a request, so a request that gives one is refused as
/// <see cref="ServerName.RefusalCode"/> when its arguments are read.
/// </summary>
public sealed record ServersRenameArgs
{
   /// <summary>Takes the new name, which must pass <see cref="ServerName"/>'s rule, and holds it with surrounding white space removed.</summary>
   public ServersRenameArgs(string name)
   {
      Name = ServerName.Require(name);
   }



   /// <summary>The server's new name, as it is stored.</summary>
   public string Name { get; }
}
