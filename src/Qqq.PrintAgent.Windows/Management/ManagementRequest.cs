/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json.Serialization;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// One request on the management pipe: the contract version, the operation, the server it acts on (absent for an
/// operation on the computer or on the list of servers) and the operation's arguments as the JSON object they arrived
/// as. The arguments stay untyped here because their type depends on the operation; <see cref="ManagementJson"/> reads
/// them into the operation's own record. Arguments can hold a session credential or an enrollment code, so neither the
/// text form of a request nor that of its arguments shows them.
/// <para>
/// A request is built only by the <see cref="For(string, string)"/> functions, which the parser uses too, and no
/// member can be set afterwards. So every request that exists is one the parser would read back: it is of this
/// contract's version, its operation has the shape of an operation's name, and its server, when it names one, is a
/// full identifier in the one spelling the wire carries. Whether the operation exists, and whether it should name a
/// server, is still decided by <see cref="ManagementOperations.Check"/>.
/// </para>
/// </summary>
[JsonConverter(typeof(ManagementJson.NotBySerializer<ManagementRequest>))]
public sealed record ManagementRequest
{
   /// <summary>Stores a request after checking the operation's name and the server's identifier. The refusals are fixed text and never repeat what was given.</summary>
   private ManagementRequest(string operation, string? server, ManagementPayload? arguments)
   {
      ArgumentNullException.ThrowIfNull(operation);
      Op = ManagementJson.IsOperationName(operation) ? operation
         : throw new ArgumentException("An operation's name is lower-case letters, optionally followed by one dot and a camel-case word, and at most 64 characters.", nameof(operation));
      Server = server is null || ManagementProtocol.IsServerId(server) ? server : throw new ArgumentException("A request names a server by its full identifier, in lower case with hyphens.", nameof(server));
      Args = arguments;
   }



   /// <summary>The contract version the request is in. It is always this contract's: a request of another version is not read, and none can be built.</summary>
   public int V { get; } = ManagementProtocol.Version;



   /// <summary>The operation's name as the wire carries it. It has the shape of a name; whether such an operation exists is <see cref="ManagementOperations.Check"/>'s to say.</summary>
   public string Op { get; }



   /// <summary>The identifier of the server the operation acts on, or null for an operation on the computer or on the list of servers.</summary>
   public string? Server { get; }



   /// <summary>The operation's arguments as the JSON object the wire carries, or null when the request has none.</summary>
   public ManagementPayload? Args { get; }



   /// <summary>Builds a request without arguments: for an operation that takes none, or one whose arguments are all optional. A name that is not shaped like an operation's, or a server that is not a full identifier, is refused.</summary>
   public static ManagementRequest For(string operation, string? server = null) => new(operation, server, null);



   /// <summary>Builds a request, writing the operation's argument record the way the wire carries it. A name that is not shaped like an operation's, or a server that is not a full identifier, is refused.</summary>
   public static ManagementRequest For<TArguments>(string operation, string? server, TArguments arguments)
      where TArguments : class
      => new(operation, server, ManagementPayload.From(arguments));



   /// <summary>Builds a request around arguments that are already the JSON object the wire carries, or around none: what the parser does with a request it read. The operation's name and the server are checked as for every request.</summary>
   public static ManagementRequest For(string operation, string? server, ManagementPayload? arguments) => new(operation, server, arguments);



   /// <summary>Names the version, the operation and the server, and nothing else, so logging a request can never reveal a session credential or an enrollment code.</summary>
   public override string ToString() => Server is null ? $"Management request v{V} {Op} (arguments not shown)" : $"Management request v{V} {Op} for server {Server} (arguments not shown)";
}
