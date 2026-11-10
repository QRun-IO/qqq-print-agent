/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// The JSON checks a message from the server goes through before any field is trusted. Every message must be a JSON
/// object within a depth limit. What is done about a repeated field name depends on who wrote the message: a realtime
/// frame is written by the server alone, so no name may repeat anywhere in it; a job carries options the server relays
/// from whoever submitted it, so there only the members the agent reads may not repeat and everything else is ignored,
/// as the contract requires of unknown fields. The checks live in one place so each reader states its rule in one call
/// and all fail with <see cref="PrintingProtocolException"/>.
/// </summary>
internal static class ProtocolJson
{
   /// <summary>The nesting the JSON reader accepts by default. Claim and queued-job responses are bounded by it and nothing tighter, because a submitter's options may nest as deep as the reader allows.</summary>
   internal const int ReaderDepth = 64;

   private static readonly byte[] ByteOrderMark = [0xEF, 0xBB, 0xBF];



   /// <summary>
   /// Parses a message as a JSON object within the depth limit. The caller owns the returned document. The subject names
   /// the message in the failure text ("realtime frame", "claim response").
   /// </summary>
   internal static JsonDocument ParseObject(ReadOnlyMemory<byte> message, string subject, int maximumDepth)
   {
      JsonDocument document;
      try
      { document = JsonDocument.Parse(message, new JsonDocumentOptions { MaxDepth = maximumDepth }); }
      catch(JsonException)
      {
         /////////////////////////////////////////////////////////////////////
         // the parser's own message can quote the offending text, so it is //
         // deliberately not passed on                                      //
         /////////////////////////////////////////////////////////////////////
         throw new PrintingProtocolException($"The {subject} is not valid JSON.");
      }
      if(document.RootElement.ValueKind != JsonValueKind.Object)
      {
         document.Dispose();
         throw new PrintingProtocolException($"The {subject} is not a JSON object.");
      }
      return document;
   }



   /// <summary>
   /// Drops a UTF-8 byte-order mark from the start of an HTTP response body. The JSON parser refuses one, but a server
   /// or a proxy in front of it may send it, and the readers the contract checks replaced accepted it.
   /// </summary>
   internal static ReadOnlyMemory<byte> WithoutByteOrderMark(ReadOnlyMemory<byte> body)
      => body.Span.StartsWith(ByteOrderMark) ? body[ByteOrderMark.Length..] : body;



   /// <summary>Rejects any object that repeats a field name, ignoring case, anywhere in the message; two parsers could otherwise read different values from the same text.</summary>
   internal static void RejectRepeatedFields(JsonElement value, string subject)
   {
      if(value.ValueKind == JsonValueKind.Object)
      {
         var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
         foreach(JsonProperty property in value.EnumerateObject())
         {
            if(!names.Add(property.Name))
            { throw new PrintingProtocolException($"The {subject} repeats a field."); }
            RejectRepeatedFields(property.Value, subject);
         }
      }
      else if(value.ValueKind == JsonValueKind.Array)
      {
         foreach(JsonElement child in value.EnumerateArray())
         { RejectRepeatedFields(child, subject); }
      }
   }



   /// <summary>
   /// True when the object spells one of the given member names more than once. Names are compared ignoring case because
   /// that is how the deserializer matches them: "copies" and "Copies" are the same member to it, so a job carrying
   /// both could print a different number of copies than the server meant. A name outside the list may repeat freely.
   /// </summary>
   internal static bool RepeatsMember(JsonElement value, IReadOnlyList<string> names)
   {
      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach(JsonProperty property in value.EnumerateObject())
      {
         if(names.Contains(property.Name, StringComparer.OrdinalIgnoreCase) && !seen.Add(property.Name))
         { return true; }
      }
      return false;
   }



   /// <summary>
   /// Finds the candidates for the member the contract names: every member spelled exactly as the contract spells it,
   /// because that is the one the server itself reads, or, when there is none, every member that matches ignoring
   /// case, as the deserializer would take it. One candidate is the member, none means it is absent, and more than one
   /// means the message does not say which value it means.
   /// </summary>
   internal static JsonElement[] Members(JsonElement value, string name)
   {
      JsonElement[] exact = [.. value.EnumerateObject().Where(property => property.NameEquals(name)).Select(property => property.Value)];
      return exact.Length != 0
         ? exact
         : [.. value.EnumerateObject().Where(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)).Select(property => property.Value)];
   }



   /// <summary>Converts a JSON object into a contract type, reporting a member of the wrong JSON type as a protocol violation.</summary>
   internal static T Read<T>(JsonElement value, string subject)
   {
      try
      { return value.Deserialize<T>(PrintApiClient.JsonOptions)!; }
      catch(JsonException)
      { throw new PrintingProtocolException($"The {subject} does not have the expected fields."); }
   }



   /// <summary>Reads a required text member of at most the given length, or reports its absence, wrong type, emptiness or excess length as a protocol violation.</summary>
   internal static string RequiredText(JsonElement value, string name, int maximumLength, string subject)
   {
      return value.TryGetProperty(name, out JsonElement member) && member.ValueKind == JsonValueKind.String && member.GetString() is { Length: >= 1 } text && text.Length <= maximumLength
         ? text
         : throw new PrintingProtocolException($"The {subject} has no usable {name}.");
   }
}
