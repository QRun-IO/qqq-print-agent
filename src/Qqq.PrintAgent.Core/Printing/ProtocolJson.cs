/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// The JSON checks every message from the server goes through before any field is trusted: bounded depth, an object at
/// the top, and no repeated field names. They live in one place so a realtime frame, a claim and a queued-job list are
/// read by the same rules and all fail with <see cref="PrintingProtocolException"/>.
/// </summary>
internal static class ProtocolJson
{
   /// <summary>
   /// Parses a message as a JSON object with a depth limit and no repeated field anywhere in it. The caller owns the
   /// returned document. The subject names the message in the failure text ("realtime frame", "claim response").
   /// </summary>
   internal static JsonDocument ParseObject(ReadOnlyMemory<byte> message, string subject, int maximumDepth)
   {
      JsonDocument document;
      try
      { document = JsonDocument.Parse(message, new JsonDocumentOptions { MaxDepth = maximumDepth }); }
      catch(JsonException)
      {
         // The parser's own message can quote the offending text, so it is deliberately not passed on.
         throw new PrintingProtocolException($"The {subject} is not valid JSON.");
      }
      try
      {
         if(document.RootElement.ValueKind != JsonValueKind.Object)
         { throw new PrintingProtocolException($"The {subject} is not a JSON object."); }
         RejectDuplicateFields(document.RootElement, subject);
         return document;
      }
      catch
      {
         document.Dispose();
         throw;
      }
   }



   /// <summary>Rejects any object that repeats a field name, ignoring case, anywhere in the message; two parsers could otherwise read different values from the same text.</summary>
   private static void RejectDuplicateFields(JsonElement value, string subject)
   {
      if(value.ValueKind == JsonValueKind.Object)
      {
         var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
         foreach(JsonProperty property in value.EnumerateObject())
         {
            if(!names.Add(property.Name))
            { throw new PrintingProtocolException($"The {subject} repeats a field."); }
            RejectDuplicateFields(property.Value, subject);
         }
      }
      else if(value.ValueKind == JsonValueKind.Array)
      {
         foreach(JsonElement child in value.EnumerateArray())
         { RejectDuplicateFields(child, subject); }
      }
   }



   /// <summary>Converts a JSON object that already passed <see cref="ParseObject"/> into a contract type, reporting a member of the wrong JSON type as a protocol violation.</summary>
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
