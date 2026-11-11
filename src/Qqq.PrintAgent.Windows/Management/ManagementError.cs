/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json.Serialization;
using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// Why a request failed. Clients decide by <see cref="Code"/>, one of the <see cref="ManagementErrorCode"/> values; the
/// message is for people and no client matches its text. A client that meets a code it does not know shows the message
/// and treats the failure as it would <see cref="ManagementErrorCode.InternalError"/>. Details are present only for the
/// codes whose table entry says so, and their type is fixed per code (<see cref="ManagementErrorCode.DetailsType"/>).
/// <para>
/// An error can be built in two ways, and both refuse a code that is not in the table: the constructor, for a code that
/// carries no details, and <see cref="WithDetails"/>, for a code that carries some, which takes the details as the
/// record the table names for that code and nothing else. So a failure cannot be sent under text that is not a code,
/// a code without details cannot be given any, and details cannot be written from an object that only looks like the
/// right record and skips what that record's own construction enforces. Only the parser builds an error otherwise, from
/// what the wire carried, because a client must be able to read a code a newer service added.
/// </para>
/// <para>
/// The message may repeat what a server said, so it is never held as given: every way of building an error ends in one
/// place that makes it printable and cuts it at <see cref="ManagementProtocol.MaximumMessageCharacters"/>, and nothing
/// can change it afterwards, because no member can be set once the error exists (a <c>with</c> expression can only copy
/// it). That holds for an error the service builds and for one a client reads from the wire, so text a server supplied
/// reaches neither the wire nor a client's screen raw.
/// </para>
/// </summary>
[JsonConverter(typeof(ManagementJson.NotBySerializer<ManagementError>))]
public sealed record ManagementError
{
   /// <summary>Builds an error with a code that carries no details. A code that is not in the table, or one whose table entry promises details, is refused.</summary>
   public ManagementError(string code, string message) : this(Known(code, detailsType: null), message, null)
   {
   }



   /// <summary>Stores an error. The message is made printable and cut here, the only place an error's message is ever set.</summary>
   private ManagementError(string code, string message, ManagementPayload? details)
   {
      Code = code;
      Message = QqqCompatibilityProblem.Printable(message, ManagementProtocol.MaximumMessageCharacters);
      Details = details;
   }



   /// <summary>What went wrong, as one of the <see cref="ManagementErrorCode"/> values. Clients decide by this.</summary>
   public string Code { get; }



   /// <summary>What went wrong, for people: printable, and bounded by <see cref="ManagementProtocol.MaximumMessageCharacters"/>.</summary>
   public string Message { get; }



   /// <summary>The details the code's table entry promises, or null for a code that carries none.</summary>
   public ManagementPayload? Details { get; }



   /// <summary>
   /// Builds an error with one of the codes that carry details, writing the details record the way the wire carries it.
   /// The record must be exactly the type the table names for the code (<see cref="ManagementErrorCode.DetailsType"/>):
   /// a code that is not in the table, a code that carries no details, another record and an object of no declared
   /// type are all refused.
   /// </summary>
   public static ManagementError WithDetails<TDetails>(string code, string message, TDetails details)
      where TDetails : class
   {
      ArgumentNullException.ThrowIfNull(details);
      return new ManagementError(Known(code, typeof(TDetails)), message, ManagementPayload.From(details));
   }



   /// <summary>
   /// Holds an error as a client read it from the wire: the code as it came, known or not, with whatever details came
   /// with it. Only the parser calls this. The message is still made printable and cut, so a service that did not bound
   /// it cannot put raw text on a client's screen.
   /// </summary>
   internal static ManagementError FromWire(string code, string message, ManagementPayload? details) => new(code, message, details);



   /// <summary>Shows the code and the message, not the details, which are read through <see cref="ManagementJson"/> as their own type.</summary>
   public override string ToString() => $"{Code}: {Message}";



   /// <summary>
   /// Returns the code when the service may send it with details of the given type (null for none), and refuses it
   /// otherwise. The refusals are fixed text: what was given as a code is never repeated, because it need not be one.
   /// </summary>
   private static string Known(string code, Type? detailsType)
   {
      ArgumentNullException.ThrowIfNull(code);
      if(!ManagementErrorCode.All.Contains(code, StringComparer.Ordinal))
      {
         throw new ArgumentException("This is not one of the management contract's error codes.", nameof(code));
      }
      if(ManagementErrorCode.DetailsType(code) != detailsType)
      {
         throw new ArgumentException(detailsType is null ? "This code carries details, and none were given." : "This code does not carry details of this type.", nameof(code));
      }
      return code;
   }
}
