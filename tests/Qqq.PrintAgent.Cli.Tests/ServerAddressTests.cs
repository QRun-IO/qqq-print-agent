/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// What a server's address may be: the one rule the service and the companion both call for the address an operator
/// types. It is held against the session client, which applies the same rule to the origin it is built for and still
/// has its own copy: every address the rule accepts is one a session client can be made for, with the same origin, and
/// every address the session client refuses the rule refuses. The rule refuses one thing more, a host with no ASCII
/// name. The characters are written as escapes so this file holds none of them.
/// </summary>
public sealed class ServerAddressTests
{
   /// <summary>
   /// An address is read when it is HTTPS, or plain HTTP for this computer itself, and names nothing but an origin. The
   /// result is the origin in the one form an origin is kept in: lower-case scheme and host, the host by its ASCII
   /// name, a default port left out, the path "/". White space around the text is not part of it. A session client
   /// can be made for the result, and has it as its own origin.
   /// </summary>
   [Theory]
   [InlineData("https://qqq.example.test", "https://qqq.example.test/")]
   [InlineData("https://qqq.example.test/", "https://qqq.example.test/")]
   [InlineData("  https://qqq.example.test/ \t", "https://qqq.example.test/")]
   [InlineData("HTTPS://QQQ.Example.Test:443/", "https://qqq.example.test/")]
   [InlineData("https://qqq.example.test:8443", "https://qqq.example.test:8443/")]
   [InlineData("https://192.168.1.10/", "https://192.168.1.10/")]
   [InlineData("https://[2001:db8::1]:8443/", "https://[2001:db8::1]:8443/")]
   [InlineData("https://b\u00FCcher.example/", "https://xn--bcher-kva.example/")]
   [InlineData("http://localhost:18581", "http://localhost:18581/")]
   [InlineData("http://127.0.0.1:18581/", "http://127.0.0.1:18581/")]
   [InlineData("http://[::1]:18581/", "http://[::1]:18581/")]
   public void TryRead_ReadsAnOriginASessionCanBeMadeFor(string text, string expected)
   {
      Assert.True(ServerAddress.TryRead(text, out Uri? origin, out ServerAddressProblem problem));

      Assert.Equal(ServerAddressProblem.None, problem);
      Assert.Equal(expected, origin.AbsoluteUri);
      Assert.Equal(origin, QqqOrigin.Of(origin));
      Assert.Equal(origin, SessionClientOrigin(origin));
      Assert.True(SessionClientAccepts(new Uri(text.Trim())));
   }



   /// <summary>Only HTTPS is a server's address. Plain HTTP is read for this computer itself (localhost and the loopback addresses), which exists for development, and for no other host; every other scheme is refused, and so is text that is no absolute address at all, or nothing.</summary>
   [Theory]
   [InlineData("https://qqq.example.test/", ServerAddressProblem.None)]
   [InlineData("http://localhost/", ServerAddressProblem.None)]
   [InlineData("http://127.0.0.1/", ServerAddressProblem.None)]
   [InlineData("http://[::1]/", ServerAddressProblem.None)]
   [InlineData("http://qqq.example.test/", ServerAddressProblem.NotSecure)]
   [InlineData("http://192.168.1.10:8080/", ServerAddressProblem.NotSecure)]
   [InlineData("http://localhost.example.test/", ServerAddressProblem.NotSecure)]
   [InlineData("wss://qqq.example.test/", ServerAddressProblem.NotSecure)]
   [InlineData("ws://localhost/", ServerAddressProblem.NotSecure)]
   [InlineData("ftp://qqq.example.test/", ServerAddressProblem.NotSecure)]
   [InlineData("file:///C:/qqq", ServerAddressProblem.NotSecure)]
   [InlineData("localhost:18581", ServerAddressProblem.NotSecure)]
   [InlineData("qqq.example.test", ServerAddressProblem.NotAnAddress)]
   [InlineData("qqq.example.test/app", ServerAddressProblem.NotAnAddress)]
   [InlineData("https://", ServerAddressProblem.NotAnAddress)]
   [InlineData("not an address", ServerAddressProblem.NotAnAddress)]
   [InlineData("", ServerAddressProblem.Empty)]
   [InlineData("   ", ServerAddressProblem.Empty)]
   [InlineData(null, ServerAddressProblem.Empty)]
   public void TryRead_HttpsOnly_PlainHttpForThisComputerOnly(string? text, ServerAddressProblem expected)
   {
      bool read = ServerAddress.TryRead(text, out Uri? origin, out ServerAddressProblem problem);

      Assert.Equal(expected, problem);
      Assert.Equal(expected == ServerAddressProblem.None, read);
      Assert.Equal(read, origin is not null);
   }



   /// <summary>
   /// An address names an origin and nothing else. A user name or a password, which must never be stored or logged with
   /// an address, a query, a fragment and a path are each refused and told apart; an address that is wrong in several
   /// ways is reported under the first in the rule's order. Nothing comes back for a refused address.
   /// </summary>
   [Theory]
   [InlineData("https://operator:synthetic-password@qqq.example.test/", ServerAddressProblem.HasUserInformation)]
   [InlineData("https://operator@qqq.example.test/", ServerAddressProblem.HasUserInformation)]
   [InlineData("https://qqq.example.test/?next=1", ServerAddressProblem.HasQueryOrFragment)]
   [InlineData("https://qqq.example.test?next=1", ServerAddressProblem.HasQueryOrFragment)]
   [InlineData("https://qqq.example.test/#top", ServerAddressProblem.HasQueryOrFragment)]
   [InlineData("https://qqq.example.test/app", ServerAddressProblem.HasPath)]
   [InlineData("https://qqq.example.test/qqq/v1/", ServerAddressProblem.HasPath)]
   [InlineData("https://qqq.example.test//", ServerAddressProblem.HasPath)]
   [InlineData("http://operator:synthetic-password@qqq.example.test/app?next=1#top", ServerAddressProblem.NotSecure)]
   [InlineData("https://operator:synthetic-password@qqq.example.test/app?next=1#top", ServerAddressProblem.HasUserInformation)]
   [InlineData("https://qqq.example.test/app?next=1#top", ServerAddressProblem.HasQueryOrFragment)]
   public void TryRead_RefusesUserInformationQueryAndFragment(string text, ServerAddressProblem expected)
   {
      Assert.False(ServerAddress.TryRead(text, out Uri? origin, out ServerAddressProblem problem));

      Assert.Equal(expected, problem);
      Assert.Null(origin);
      Assert.False(SessionClientAccepts(new Uri(text)));
   }



   /// <summary>
   /// A host with no plain ASCII name is refused, because that name is what is stored, compared and shown: a host that
   /// holds a line separator, and one the platform will not convert. This is the one thing the rule refuses that the
   /// session client, which is handed an origin and leaves the naming to whoever stores it, does not.
   /// </summary>
   [Theory]
   [InlineData("https://id\u2028.example.test/")]
   [InlineData("https://-b\u00E4d-.example.test/")]
   public void TryRead_RefusesAHostWithNoAsciiName(string text)
   {
      Assert.False(ServerAddress.TryRead(text, out Uri? origin, out ServerAddressProblem problem));

      Assert.Equal(ServerAddressProblem.HostNotNameable, problem);
      Assert.Null(origin);
      Assert.Null(QqqOrigin.Of(new Uri(text)));
      Assert.True(SessionClientAccepts(new Uri(text)));
   }



   /// <summary>
   /// The rule and the session client agree on every address in a table of the shapes an operator can type: the rule
   /// reads an address exactly when a session client can be made for it, except for a host with no ASCII name, which
   /// only the rule refuses. When both accept, the origin the rule returns is the origin the session client holds.
   /// The values in the order they are checked are the rule's whole vocabulary.
   /// </summary>
   [Fact]
   public void TheRule_AgreesWithWhatTheSessionClientAcceptsAndRefuses()
   {
      string[] table =
      [
         "https://qqq.example.test", "https://qqq.example.test/", "https://QQQ.example.test:443/", "https://qqq.example.test:8443/", "https://10.0.0.7/", "https://[::1]/",
         "https://xn--bcher-kva.example/", "http://localhost/", "http://localhost:18581/", "http://127.0.0.1:8080/", "http://127.8.9.10/", "http://[::1]:8080/",
         "http://qqq.example.test/", "http://10.0.0.7/", "http://localhost.example.test/", "wss://qqq.example.test/", "ws://localhost/", "ftp://qqq.example.test/",
         "io.qrun.qqq-print-agent:/oauth/callback", "mailto:operator@example.test", "https://operator@qqq.example.test/", "https://operator:synthetic@qqq.example.test/",
         "http://operator@localhost/", "https://qqq.example.test/?", "https://qqq.example.test/?a=1", "https://qqq.example.test/#", "https://qqq.example.test/#top",
         "https://qqq.example.test/app", "https://qqq.example.test/app/", "https://qqq.example.test//", "http://localhost/app", "https://id\u2028.example.test/",
      ];

      Assert.All(table, text =>
      {
         Uri address = new(text);
         bool read = ServerAddress.TryRead(text, out Uri? origin, out ServerAddressProblem problem);
         Uri? made = SessionClientOrigin(address);
         Assert.True(read == made is not null || (made is not null && problem == ServerAddressProblem.HostNotNameable), text);
         Assert.True(!read || origin == made, text);
      });
      Assert.Equal([ServerAddressProblem.None, ServerAddressProblem.Empty, ServerAddressProblem.NotAnAddress, ServerAddressProblem.NotSecure, ServerAddressProblem.HasUserInformation,
         ServerAddressProblem.HasQueryOrFragment, ServerAddressProblem.HasPath, ServerAddressProblem.HostNotNameable], Enum.GetValues<ServerAddressProblem>());
   }



   /// <summary>True when a session client can be made for an address.</summary>
   private static bool SessionClientAccepts(Uri address) => SessionClientOrigin(address) is not null;



   /// <summary>The origin of the session client made for an address the way the service and the companion make one, with plain HTTP allowed for this computer itself only, or null when the session client refuses the address. It contacts nothing.</summary>
   private static Uri? SessionClientOrigin(Uri address)
   {
      try
      {
         return new QqqSessionClient(address, allowLoopbackHttp: address.IsLoopback).Origin;
      }
      catch(ArgumentException)
      {
         return null;
      }
   }
}
