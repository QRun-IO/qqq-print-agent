/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>
/// Names one page: its kind and, for a page that belongs to a server, that server's identifier. Two keys are the same
/// page when both parts are equal, so a key can be compared, kept and used to look a page's message up. A key can
/// only be made in the ways below, so a server's page always names a server by an identifier in the one form the
/// contract writes, and a page of the computer never names one.
/// </summary>
public sealed record PageKey
{
   /// <summary>Stores the two parts; the members below are the only callers.</summary>
   private PageKey(PageKind kind, string? serverId)
   {
      Kind = kind;
      ServerId = serverId;
   }



   /// <summary>Which kind of page this is.</summary>
   public PageKind Kind { get; }



   /// <summary>The server the page belongs to, or null for a page of the computer.</summary>
   public string? ServerId { get; }



   /// <summary>The connection wizard's page.</summary>
   public static PageKey Wizard { get; } = new(PageKind.Wizard, null);



   /// <summary>The Computer page.</summary>
   public static PageKey Computer { get; } = new(PageKind.Computer, null);



   /// <summary>The Logs page.</summary>
   public static PageKey Logs { get; } = new(PageKind.Logs, null);



   /// <summary>The Settings page.</summary>
   public static PageKey Settings { get; } = new(PageKind.Settings, null);



   /// <summary>A server's overview.</summary>
   public static PageKey Server(string serverId) => ForServer(PageKind.Server, serverId);



   /// <summary>
   /// One of a server's pages: its overview, Printers, Jobs or Connection. Any other kind is refused, and so is text
   /// that is not a server identifier as the contract writes it. The refusals are fixed text and repeat nothing that
   /// was given.
   /// </summary>
   public static PageKey ForServer(PageKind kind, string serverId)
   {
      if(kind is not (PageKind.Server or PageKind.Printers or PageKind.Jobs or PageKind.Connection))
      {
         throw new ArgumentException("This kind of page does not belong to a server.", nameof(kind));
      }
      if(!ManagementProtocol.IsServerId(serverId))
      {
         throw new ArgumentException("This is not a server's identifier.", nameof(serverId));
      }
      return new PageKey(kind, serverId);
   }



   /// <summary>The key as a log entry shows it: the kind and, for a server's page, the server's identifier. It never holds a server's name.</summary>
   public override string ToString() => ServerId is null ? Kind.ToString() : Kind + " " + ServerId;
}
