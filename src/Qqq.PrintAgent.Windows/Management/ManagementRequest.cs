/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// One request on the management pipe: the contract version, the operation, the server it acts on (absent for an
/// operation on the computer or on the list of servers) and the operation's arguments as the JSON object they arrived
/// as. The arguments stay untyped here because their type depends on the operation; <see cref="ManagementJson"/> reads
/// them into the operation's own record. Arguments can hold a session credential or an enrollment code, so the text
/// form of a request never includes them.
/// </summary>
public sealed record ManagementRequest(int V, string Op, string? Server = null, JsonElement? Args = null)
{
   /// <summary>Builds a request of this contract's version for an operation that takes no arguments.</summary>
   public static ManagementRequest For(string operation, string? server = null) => new(ManagementProtocol.Version, operation, server);



   /// <summary>Builds a request of this contract's version, writing the operation's argument record the way the wire carries it.</summary>
   public static ManagementRequest For<TArguments>(string operation, string? server, TArguments arguments)
      where TArguments : class
      => new(ManagementProtocol.Version, operation, server, JsonSerializer.SerializeToElement(arguments, ManagementJson.Options));



   /// <summary>Names the version, the operation and the server, and nothing else, so logging a request can never reveal a session credential or an enrollment code.</summary>
   public override string ToString() => Server is null ? $"Management request v{V} {Op} (arguments not shown)" : $"Management request v{V} {Op} for server {Server} (arguments not shown)";
}
