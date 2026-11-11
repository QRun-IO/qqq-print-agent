/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using System.Text.Json.Serialization;

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
/// what the wire carried, because a client must be able to read a code a newer service added; that way in
/// (<see cref="FromWire"/>) takes the error object as JSON and nothing else, so no code can use it to turn loose text
/// into an error.
/// </para>
/// <para>
/// The message may repeat what a server said, so it is never held as given: every way of building an error ends in one
/// place that gives it the form of text on the wire (<see cref="ManagementText"/>: printable, and cut at
/// <see cref="ManagementProtocol.MaximumMessageCharacters"/> exactly), and nothing can change it afterwards, because
/// no member can be set once the error exists (a <c>with</c> expression can only copy it). That holds for an error the
/// service builds and for one a client reads from the wire, so text a server supplied reaches neither the wire nor a
/// client's screen raw. A reply whose code or message is not text at all, because it holds the escape of a surrogate
/// without its partner, is not read as a reply (<see cref="ManagementEnvelopeProblem.ReplyIncomplete"/>), so no error
/// is ever held with one. An error the service builds must also say something: a message with nothing to show is
/// refused, and no stand-in text is sent in its place. What a client reads is not refused for that, so that it still
/// learns the code.
/// </para>
/// </summary>
[JsonConverter(typeof(ManagementJson.NotBySerializer<ManagementError>))]
public sealed record ManagementError
{
   /// <summary>Builds an error with a code that carries no details. A code that is not in the table, or one whose table entry promises details, is refused, and so is a message with nothing to show.</summary>
   public ManagementError(string code, string message) : this(Known(code, detailsType: null), ManagementText.Message(message), null)
   {
   }



   /// <summary>Stores an error. The message is made printable and cut here, the only place an error's message is ever set; for a message that already has that form, this changes nothing.</summary>
   private ManagementError(string code, string message, ManagementPayload? details)
   {
      Code = code;
      Message = ManagementText.Bounded(message, ManagementProtocol.MaximumMessageCharacters);
      Details = details;
   }



   /// <summary>What went wrong, as one of the <see cref="ManagementErrorCode"/> values. Clients decide by this.</summary>
   public string Code { get; }



   /// <summary>What went wrong, for people: printable, and at most <see cref="ManagementProtocol.MaximumMessageCharacters"/> UTF-16 units.</summary>
   public string Message { get; }



   /// <summary>The details the code's table entry promises, or null for a code that carries none.</summary>
   public ManagementPayload? Details { get; }



   /// <summary>
   /// Builds an error with one of the codes that carry details, writing the details record the way the wire carries it.
   /// The record must be exactly the type the table names for the code (<see cref="ManagementErrorCode.DetailsType"/>):
   /// a code that is not in the table, a code that carries no details, another record and an object of no declared
   /// type are all refused, and so is a message with nothing to show.
   /// </summary>
   public static ManagementError WithDetails<TDetails>(string code, string message, TDetails details)
      where TDetails : class
   {
      ArgumentNullException.ThrowIfNull(details);
      return new ManagementError(Known(code, typeof(TDetails)), ManagementText.Message(message), ManagementPayload.From(details));
   }



   /// <summary>
   /// Reads an error object as a reply carried it, or returns null when it is not an object or lacks a code or a
   /// message as text (a code or a message that holds a surrogate without its partner is not text). The code is held
   /// as it came, known or not, with whatever details came with it; details of any kind but an object are dropped,
   /// because every details type is an object. The message is still made printable and cut, so a service that did not
   /// bound it cannot put raw text on a client's screen; an empty one is held as it came. This is the parser's way
   /// in, and it is the only one that does not check the code against the table: it takes the JSON the wire carried
   /// and not a code and a message as text, so a handler cannot reach for it to send a code of its own making.
   /// </summary>
   internal static ManagementError? FromWire(JsonElement error)
   {
      if(error.ValueKind != JsonValueKind.Object
         || !error.TryGetProperty("code", out JsonElement code) || code.ValueKind != JsonValueKind.String || ManagementJson.TextOf(code) is not { } codeText
         || !error.TryGetProperty("message", out JsonElement message) || message.ValueKind != JsonValueKind.String || ManagementJson.TextOf(message) is not { } messageText)
      {
         return null;
      }
      ManagementPayload? details = error.TryGetProperty("details", out JsonElement found) && found.ValueKind == JsonValueKind.Object ? ManagementPayload.FromElement(found) : null;
      return new ManagementError(codeText, messageText, details);
   }



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
