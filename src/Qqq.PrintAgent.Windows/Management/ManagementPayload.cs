/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The JSON object an envelope carries untyped: a request's arguments, a reply's result or an error's details. Its type
/// depends on the operation or the code, so the envelope holds it as it arrived and <see cref="ManagementJson"/> reads it
/// into the right record. It is a type of its own, not a bare JSON element, because a JSON element prints its whole
/// content when it is put into a log line or an interpolated string, and arguments can hold a session credential or an
/// enrollment code and one result holds the provider's sign-out request. The text form of a payload is fixed.
/// </summary>
public sealed class ManagementPayload
{
   /// <summary>The JSON object, copied so it outlives the document it was read from. It is private so nothing prints it by reaching through the payload.</summary>
   private readonly JsonElement _element;



   /// <summary>Holds a copy of the element that does not depend on the document it was read from.</summary>
   private ManagementPayload(JsonElement element)
   {
      _element = element.Clone();
   }



   /// <summary>Writes a record the way the wire carries it.</summary>
   public static ManagementPayload From<TValue>(TValue value)
      where TValue : class
      => new(JsonSerializer.SerializeToElement(value, ManagementJson.Options));



   /// <summary>Wraps a JSON object that was read from the wire. Anything but an object is refused, because every argument, result and details record is one.</summary>
   public static ManagementPayload FromElement(JsonElement element)
      => element.ValueKind == JsonValueKind.Object ? new ManagementPayload(element) : throw new ArgumentException("A management payload is a JSON object.", nameof(element));



   /// <summary>The JSON object, for the serializer. Printing what this returns shows the content; the payload itself never does.</summary>
   public JsonElement ToElement() => _element;



   /// <summary>Returns a fixed label, so a payload in a log line or an interpolated string can never reveal a credential, an enrollment code or the provider's sign-out request.</summary>
   public override string ToString() => "(management payload not shown)";
}
