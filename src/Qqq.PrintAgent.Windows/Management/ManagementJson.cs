/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The one way version 2 of the management pipe is written and read. Names on the wire are camel case; a member that
/// has no value is left out; dates are ISO 8601 in UTC; the text is one line, so it fits the pipe's newline framing.
/// Nothing is read from an object that has the same member twice, at any depth, because readers disagree about which
/// value counts. A required member that is missing, a null where a value is needed and a null inside a list the
/// record knows make the whole record unreadable, in a request and in a reply alike: a record with a hole in it is
/// not half-used. Requests are also read strictly about what they may contain: the envelope is checked member by
/// member and arguments refuse a member they do not know, because a request the service does not fully understand
/// must not be half-obeyed. Replies skip a member they do not know, whatever it holds, so a newer service can add one
/// without breaking an older client; for the same reason a client treats a vocabulary value it does not know (a
/// health, a stage, an outcome) as unknown, never as a failure to read the reply. An envelope is read only by
/// <see cref="ParseRequest"/> and <see cref="ParseReply"/>: handed to the serializer directly, the envelope types
/// refuse to be read, so the checks those two make cannot be walked around by accident. The refusal is declared on
/// the four envelope types themselves (<see cref="NotBySerializer{TEnvelope}"/>), so it holds for the default
/// options and for any options object that does not bring a converter of its own for one of these types. Such a
/// converter, placed in the options, takes precedence over a declaration on the type; it has to be written for the
/// purpose, and nothing in the product has one. The same types refuse to be written by the serializer, and an
/// envelope is written only by <see cref="Write(ManagementRequest)"/> and <see cref="Write(ManagementReply)"/>, so it
/// cannot leave in another form than the one line this contract defines (other options could indent it across lines).
/// </summary>
public static class ManagementJson
{
   /// <summary>How a document is parsed: no comments, no trailing commas, a bounded depth, and no object with a repeated member.</summary>
   private static readonly JsonDocumentOptions s_documentOptions = new() { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32, AllowDuplicateProperties = false };



   /// <summary>The same, but tolerating a repeated member. It is used only to find out whether a repeated member is the reason the strict parse refused a text, so that case can be told apart from text that is not JSON at all.</summary>
   private static readonly JsonDocumentOptions s_repeatTolerantOptions = s_documentOptions with { AllowDuplicateProperties = true };



   /// <summary>The empty object that stands for arguments a request left out.</summary>
   private static readonly JsonElement s_noArguments = JsonSerializer.SerializeToElement(new NoResult());



   /// <summary>The options every argument, result and details record is written with, and results and details are read with: a member the reader does not know is skipped. The envelope around them is written by <see cref="Write(ManagementRequest)"/> and <see cref="Write(ManagementReply)"/>, not through these.</summary>
   public static JsonSerializerOptions Options { get; } = CreateOptions(skipUnknownMembers: true);



   /// <summary>The options request arguments are read with: a member the reader does not know is refused.</summary>
   public static JsonSerializerOptions StrictOptions { get; } = CreateOptions(skipUnknownMembers: false);



   /// <summary>Writes a request as the one line the pipe carries.</summary>
   public static string Write(ManagementRequest request)
   {
      ArgumentNullException.ThrowIfNull(request);
      return WriteLine(writer => WriteRequest(writer, request));
   }



   /// <summary>Writes a reply as the one line the pipe carries.</summary>
   public static string Write(ManagementReply reply)
   {
      ArgumentNullException.ThrowIfNull(reply);
      return WriteLine(writer => WriteReply(writer, reply));
   }



   /// <summary>
   /// Reads a request's envelope and tells apart everything that can be wrong with it. Size is checked before anything is
   /// parsed. The version is checked before the other members, because a request of another version may rightly have
   /// members this one does not know. After that the envelope allows exactly <c>v</c>, <c>op</c>, <c>server</c> and
   /// <c>args</c>. Whether the operation exists, and whether it should name a server, is not decided here but by
   /// <see cref="ManagementOperations.Check"/>, and the arguments are read by <see cref="TryReadArguments"/>.
   /// <para>
   /// Nothing a text holds makes this throw. JSON's grammar lets a string hold the escape of a surrogate without its
   /// partner (<c>\ud800</c> on its own), which is not text. It is answered like any other value that is wrong where
   /// it stands: in <c>op</c> the operation is malformed, in <c>server</c> the server is, and in a member's name, at
   /// any depth, the text is not JSON, because the parse cannot know that no member is repeated. In a value of the
   /// arguments it leaves the envelope readable, and the arguments are then not read.
   /// </para>
   /// </summary>
   public static ManagementRequestParse ParseRequest(string? text)
   {
      ManagementEnvelopeProblem problem = ParseObject(text, ManagementProtocol.MaximumRequestCharacters, out JsonDocument? document);
      if(document is null)
      {
         return new ManagementRequestParse(null, problem);
      }
      using(document)
      {
         return ReadRequest(document.RootElement);
      }
   }



   /// <summary>
   /// Reads a reply's envelope. A success must carry a result object and a failure an error with a code and a message;
   /// members this version does not know are skipped. The result and the details are read into their own types by
   /// <see cref="TryReadResult"/> and <see cref="TryReadDetails"/>. Nothing a text holds makes this throw: a code or a
   /// message that holds the escape of a surrogate without its partner is not text, so the reply is incomplete, and
   /// such an escape in a member's name makes the text not JSON, as it does for a request.
   /// </summary>
   public static ManagementReplyParse ParseReply(string? text)
   {
      ManagementEnvelopeProblem problem = ParseObject(text, ManagementProtocol.MaximumReplyCharacters, out JsonDocument? document);
      if(document is null)
      {
         return new ManagementReplyParse(null, problem);
      }
      using(document)
      {
         return ReadReply(document.RootElement);
      }
   }



   /// <summary>
   /// Reads a request's arguments into the operation's own record, strictly. Arguments that were left out are read as
   /// an empty object, so a request without <c>args</c> is valid exactly when every member of the record is optional.
   /// It returns false when a member is unknown, repeated, missing or of the wrong kind, or when a value is refused by
   /// the type it belongs to (a session credential that is not a valid one, for instance); the caller answers
   /// <see cref="ManagementErrorCode.InvalidRequest"/>. What was wrong is not returned, because it may quote a secret.
   /// </summary>
   public static bool TryReadArguments<TArguments>(ManagementRequest request, [NotNullWhen(true)] out TArguments? arguments)
      where TArguments : class
   {
      ArgumentNullException.ThrowIfNull(request);
      arguments = Read<TArguments>(request.Args?.ToElement() ?? s_noArguments, StrictOptions);
      return arguments is not null;
   }



   /// <summary>Reads a successful reply's result into the operation's own record, skipping members this version does not know. It returns false when the reply has no result, or the result lacks a required member or otherwise does not fit the type.</summary>
   public static bool TryReadResult<TResult>(ManagementReply reply, [NotNullWhen(true)] out TResult? result)
      where TResult : class
   {
      ArgumentNullException.ThrowIfNull(reply);
      result = reply.Result is { } payload ? Read<TResult>(payload.ToElement(), Options) : null;
      return result is not null;
   }



   /// <summary>Reads an error's details into the type its code names, skipping members this version does not know. It returns false when the error has no details or they do not fit the type.</summary>
   public static bool TryReadDetails<TDetails>(ManagementError error, [NotNullWhen(true)] out TDetails? details)
      where TDetails : class
   {
      ArgumentNullException.ThrowIfNull(error);
      details = error.Details is { } payload ? Read<TDetails>(payload.ToElement(), Options) : null;
      return details is not null;
   }



   /// <summary>True for text shaped like an operation name: lower-case letters, optionally followed by one dot and a camel-case word, at most 64 characters. A name of another shape cannot be an operation of any version, so it is refused as malformed, not looked up.</summary>
   internal static bool IsOperationName(string name)
   {
      int dot = name.IndexOf('.', StringComparison.Ordinal);
      string area = dot < 0 ? name : name[..dot];
      string action = dot < 0 ? "none" : name[(dot + 1)..];
      return name.Length <= 64 && area.Length > 0 && area.All(char.IsAsciiLetterLower)
         && action.Length > 0 && char.IsAsciiLetterLower(action[0]) && action.All(char.IsAsciiLetter);
   }



   /// <summary>Builds the serializer options. Both sets write the same text and are equally strict about repeated, missing and null members; they differ only in whether a reader skips a member it does not know.</summary>
   private static JsonSerializerOptions CreateOptions(bool skipUnknownMembers)
   {
      JsonSerializerOptions options = new()
      {
         PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
         DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
         PropertyNameCaseInsensitive = false,
         NumberHandling = JsonNumberHandling.Strict,
         ReadCommentHandling = JsonCommentHandling.Disallow,
         AllowTrailingCommas = false,
         MaxDepth = 32,
         AllowDuplicateProperties = false,
         UnmappedMemberHandling = skipUnknownMembers ? JsonUnmappedMemberHandling.Skip : JsonUnmappedMemberHandling.Disallow,
         RespectRequiredConstructorParameters = true,
         RespectNullableAnnotations = true,
         TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { LetNullableMembersBeAbsent } },
      };
      options.Converters.Add(new UtcDateTimeConverter());
      options.MakeReadOnly();
      return options;
   }



   /// <summary>
   /// Lets a member that may be null be absent. Such a member is left out of the text when it is null, so its absence
   /// must read back as null even where the record gives its constructor parameter no default (a nullable member that
   /// comes before a required one cannot have a default). A member that may not be null and has no default stays
   /// required; one with a default stays optional. A member its record marks as required, by the attribute or by the
   /// language's own keyword, stays required even when it may be null: there the record has said that the member must
   /// be written, null or not.
   /// </summary>
   private static void LetNullableMembersBeAbsent(JsonTypeInfo type)
   {
      foreach(JsonPropertyInfo member in type.Properties.Where(member => member.IsRequired && member.IsSetNullable && !IsMarkedRequired(member)))
      {
         member.IsRequired = false;
      }
   }



   /// <summary>
   /// True for a member whose declaration carries <see cref="JsonRequiredAttribute"/> or the <c>required</c> keyword.
   /// The declaration is always there to ask: the resolver these options use builds every member from a property of
   /// the record and records that property as the member's attribute provider.
   /// </summary>
   private static bool IsMarkedRequired(JsonPropertyInfo member)
      => member.AttributeProvider!.IsDefined(typeof(JsonRequiredAttribute), inherit: true) || member.AttributeProvider.IsDefined(typeof(RequiredMemberAttribute), inherit: true);



   /// <summary>
   /// Checks the size, parses the text and requires an object. On success the document is handed to the caller, who
   /// disposes it. Text the strict parse refuses is parsed once more with repeated members tolerated, only to learn
   /// whether a repeated member was the reason.
   /// </summary>
   private static ManagementEnvelopeProblem ParseObject(string? text, int maximumCharacters, out JsonDocument? document)
   {
      document = null;
      if(text is null)
      {
         return ManagementEnvelopeProblem.NotJson;
      }
      if(text.Length > maximumCharacters)
      {
         return ManagementEnvelopeProblem.TooLarge;
      }
      JsonDocument parsed;
      try
      {
         parsed = JsonDocument.Parse(text, s_documentOptions);
      }
      catch(JsonException)
      {
         return IsJsonWithARepeatedMember(text) ? ManagementEnvelopeProblem.RepeatedMember : ManagementEnvelopeProblem.NotJson;
      }
      catch(Exception exception) when(exception is InvalidOperationException or ArgumentException)
      {
         /////////////////////////////////////////////////////////////////////////////////////////
         // The text holds a surrogate without its partner where the parse has to read it: as  //
         // itself, so the text cannot be carried as UTF-8 at all, or as an escape in a        //
         // member's name, which the parse must read to know that no member is repeated. The   //
         // parser reports neither as a JSON error. Nothing in such a text can be trusted.     //
         /////////////////////////////////////////////////////////////////////////////////////////
         return ManagementEnvelopeProblem.NotJson;
      }
      if(parsed.RootElement.ValueKind != JsonValueKind.Object)
      {
         parsed.Dispose();
         return ManagementEnvelopeProblem.NotAnObject;
      }
      document = parsed;
      return ManagementEnvelopeProblem.None;
   }



   /// <summary>True when text the strict parse refused is well-formed JSON once a repeated member is tolerated, which means a repeated member is what was wrong with it.</summary>
   private static bool IsJsonWithARepeatedMember(string text)
   {
      try
      {
         using var tolerated = JsonDocument.Parse(text, s_repeatTolerantOptions);
         return true;
      }
      catch(JsonException)
      {
         return false;
      }
   }



   /// <summary>Says what is wrong with an envelope's version, or nothing: it must be present and be exactly this contract's version as a whole number.</summary>
   private static ManagementEnvelopeProblem CheckVersion(JsonElement root)
   {
      if(!root.TryGetProperty("v", out JsonElement version))
      {
         return ManagementEnvelopeProblem.VersionMissing;
      }
      return version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out int value) && value == ManagementProtocol.Version
         ? ManagementEnvelopeProblem.None : ManagementEnvelopeProblem.VersionUnsupported;
   }



   /// <summary>Reads the members of a request object whose root is known to be an object and to repeat no member.</summary>
   private static ManagementRequestParse ReadRequest(JsonElement root)
   {
      ManagementEnvelopeProblem problem = CheckVersion(root);
      string? operation = null;
      string? server = null;
      ManagementPayload? arguments = null;
      foreach(JsonProperty member in root.EnumerateObject())
      {
         if(problem != ManagementEnvelopeProblem.None)
         {
            break;
         }
         JsonValueKind kind = member.Value.ValueKind;
         problem = member.Name switch
         {
            "v" => ManagementEnvelopeProblem.None,
            "op" => kind != JsonValueKind.String ? ManagementEnvelopeProblem.OperationMissing : TextOf(member.Value) is null ? ManagementEnvelopeProblem.OperationMalformed : ManagementEnvelopeProblem.None,
            "server" => kind == JsonValueKind.String && ManagementProtocol.IsServerId(TextOf(member.Value)) ? ManagementEnvelopeProblem.None : ManagementEnvelopeProblem.ServerMalformed,
            "args" => kind == JsonValueKind.Object ? ManagementEnvelopeProblem.None : ManagementEnvelopeProblem.ArgumentsNotAnObject,
            _ => ManagementEnvelopeProblem.UnexpectedMember,
         };
         operation = member.NameEquals("op") ? TextOf(member.Value) : operation;
         server = member.NameEquals("server") ? TextOf(member.Value) : server;
         arguments = member.NameEquals("args") && kind == JsonValueKind.Object ? ManagementPayload.FromElement(member.Value) : arguments;
      }
      if(problem == ManagementEnvelopeProblem.None && operation is null)
      {
         problem = ManagementEnvelopeProblem.OperationMissing;
      }
      if(problem == ManagementEnvelopeProblem.None && !IsOperationName(operation!))
      {
         problem = ManagementEnvelopeProblem.OperationMalformed;
      }
      return problem == ManagementEnvelopeProblem.None
         ? new ManagementRequestParse(new ManagementRequest(ManagementProtocol.Version, operation!, server, arguments), ManagementEnvelopeProblem.None)
         : new ManagementRequestParse(null, problem);
   }



   /// <summary>Reads the members of a reply object whose root is known to be an object.</summary>
   private static ManagementReplyParse ReadReply(JsonElement root)
   {
      ManagementEnvelopeProblem problem = CheckVersion(root);
      if(problem != ManagementEnvelopeProblem.None)
      {
         return new ManagementReplyParse(null, problem);
      }
      bool hasResult = root.TryGetProperty("result", out JsonElement result) && result.ValueKind == JsonValueKind.Object;
      ManagementError? error = root.TryGetProperty("error", out JsonElement failure) ? ReadError(failure) : null;
      ManagementReply? reply = root.TryGetProperty("ok", out JsonElement ok) switch
      {
         true when ok.ValueKind == JsonValueKind.True && hasResult => new ManagementReply(ManagementProtocol.Version, true, ManagementPayload.FromElement(result)),
         true when ok.ValueKind == JsonValueKind.False && error is not null => new ManagementReply(ManagementProtocol.Version, false, Error: error),
         _ => null,
      };
      return new ManagementReplyParse(reply, reply is null ? ManagementEnvelopeProblem.ReplyIncomplete : ManagementEnvelopeProblem.None);
   }



   /// <summary>
   /// Reads an error object, or returns null when it lacks a code or a message as text; a code or a message that
   /// holds a surrogate without its partner is not text (<see cref="TextOf"/>). Details of any kind but an object are
   /// dropped, because every details type is an object.
   /// </summary>
   private static ManagementError? ReadError(JsonElement error)
   {
      if(error.ValueKind != JsonValueKind.Object
         || !error.TryGetProperty("code", out JsonElement code) || code.ValueKind != JsonValueKind.String || TextOf(code) is not { } codeText
         || !error.TryGetProperty("message", out JsonElement message) || message.ValueKind != JsonValueKind.String || TextOf(message) is not { } messageText)
      {
         return null;
      }
      ManagementPayload? details = error.TryGetProperty("details", out JsonElement found) && found.ValueKind == JsonValueKind.Object ? ManagementPayload.FromElement(found) : null;
      return ManagementError.FromWire(codeText, messageText, details);
   }



   /// <summary>
   /// The text of a JSON string, or null when it has none to give. JSON's grammar lets a string hold the escape of a
   /// surrogate without its partner (<c>\ud800</c> on its own), which is not text: it cannot be turned into a string,
   /// and the platform throws when asked to. The envelope's own strings are read through here, so such a value is
   /// answered like any other value that is not what its member needs, and never thrown.
   /// </summary>
   private static string? TextOf(JsonElement value)
   {
      try
      {
         return value.GetString();
      }
      catch(InvalidOperationException)
      {
         return null;
      }
   }



   /// <summary>
   /// Reads one element into a type, returning null for anything that does not fit it instead of throwing: a shape the
   /// serializer refuses, a value the type's own constructor refuses, or a list of the record's with a null in it. The
   /// serializer's own null checks do not reach inside a list, and no list in this contract may hold a null.
   /// </summary>
   private static TValue? Read<TValue>(JsonElement element, JsonSerializerOptions options)
      where TValue : class
   {
      try
      {
         return HasNullInAKnownList(element, typeof(TValue), options) ? null : element.Deserialize<TValue>(options);
      }
      catch(Exception exception) when(exception is JsonException or ArgumentException or FormatException or InvalidOperationException or NotSupportedException)
      {
         return null;
      }
   }



   /// <summary>
   /// True when a list the type knows, at any depth, has a null among its items. The element is walked beside the
   /// type the serializer will read it into, so only members of the record are looked at: a member this version does
   /// not know is skipped whole, as the serializer skips it, and what a later version puts in it (a list with a null
   /// included) cannot make today's reader refuse the reply. The walk goes wherever a list can stand: through a
   /// record's members, a list's items, the values of a dictionary (which are held to the same rule as a list's
   /// items) and a value type that may be null. No record of the contract has a dictionary or such a value type
   /// today; the walk follows them so that one added later is not a way around the rule.
   /// </summary>
   private static bool HasNullInAKnownList(JsonElement element, Type type, JsonSerializerOptions options)
   {
      JsonTypeInfo known = options.GetTypeInfo(Nullable.GetUnderlyingType(type) ?? type);
      return (known.Kind, element.ValueKind) switch
      {
         (JsonTypeInfoKind.Enumerable, JsonValueKind.Array) => element.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Null || HasNullInAKnownList(item, known.ElementType!, options)),
         (JsonTypeInfoKind.Dictionary, JsonValueKind.Object) => element.EnumerateObject().Any(entry => entry.Value.ValueKind == JsonValueKind.Null || HasNullInAKnownList(entry.Value, known.ElementType!, options)),
         (JsonTypeInfoKind.Object, JsonValueKind.Object) => element.EnumerateObject().Any(member => MemberType(known, member.Name) is { } memberType && HasNullInAKnownList(member.Value, memberType, options)),
         _ => false,
      };
   }



   /// <summary>The type of the record's member with this name on the wire, or null when the record has none.</summary>
   private static Type? MemberType(JsonTypeInfo record, string name) => record.Properties.FirstOrDefault(member => string.Equals(member.Name, name, StringComparison.Ordinal))?.PropertyType;



   /// <summary>
   /// Writes one envelope as text. The envelope is written here and not by the serializer, so its form does not depend
   /// on any options object: one line, never indented, with the escaping the serializer's own default gives, which is
   /// also what the arguments, results and details inside it were written with. A payload that was read from the
   /// wire can hold, in a value, the escape of a surrogate without its partner; that is not text and cannot be
   /// written, and the failure is a <see cref="JsonException"/>, as it was when the serializer wrote the envelope.
   /// </summary>
   private static string WriteLine(Action<Utf8JsonWriter> write)
   {
      ArrayBufferWriter<byte> buffer = new();
      using(Utf8JsonWriter writer = new(buffer))
      {
         try
         {
            write(writer);
         }
         catch(InvalidOperationException exception)
         {
            throw new JsonException("The envelope holds a value that is not text (a surrogate without its partner) and cannot be written.", exception);
         }
      }
      return Encoding.UTF8.GetString(buffer.WrittenSpan);
   }



   /// <summary>Writes a request's envelope: the version, the operation, the server when one is named and the arguments when there are any.</summary>
   private static void WriteRequest(Utf8JsonWriter writer, ManagementRequest request)
   {
      writer.WriteStartObject();
      writer.WriteNumber("v", request.V);
      writer.WriteString("op", request.Op);
      if(request.Server is not null)
      {
         writer.WriteString("server", request.Server);
      }
      WritePayload(writer, "args", request.Args);
      writer.WriteEndObject();
   }



   /// <summary>Writes a reply's envelope: the version, whether the operation succeeded, and the result or the error, whichever the reply holds.</summary>
   private static void WriteReply(Utf8JsonWriter writer, ManagementReply reply)
   {
      writer.WriteStartObject();
      writer.WriteNumber("v", reply.V);
      writer.WriteBoolean("ok", reply.Ok);
      WritePayload(writer, "result", reply.Result);
      if(reply.Error is not null)
      {
         writer.WritePropertyName("error");
         WriteError(writer, reply.Error);
      }
      writer.WriteEndObject();
   }



   /// <summary>Writes an error: its code, its message and its details when it has any.</summary>
   private static void WriteError(Utf8JsonWriter writer, ManagementError error)
   {
      writer.WriteStartObject();
      writer.WriteString("code", error.Code);
      writer.WriteString("message", error.Message);
      WritePayload(writer, "details", error.Details);
      writer.WriteEndObject();
   }



   /// <summary>Writes a payload as a member of the object being written, or nothing when there is none.</summary>
   private static void WritePayload(Utf8JsonWriter writer, string name, ManagementPayload? payload)
   {
      if(payload is not null)
      {
         writer.WritePropertyName(name);
         payload.ToElement().WriteTo(writer);
      }
   }



   /// <summary>
   /// Writes every date and time as ISO 8601 in UTC with a trailing Z, whatever offset the value had, and reads back only
   /// ISO 8601 that says which instant it means: with a trailing Z or an offset. A date and time without one, a date
   /// alone and any other notation are refused, so two computers in different time zones cannot read one text as two
   /// instants.
   /// </summary>
   private sealed class UtcDateTimeConverter : JsonConverter<DateTimeOffset>
   {
      /// <summary>Reads an ISO 8601 date and time that ends in Z or in an offset, as UTC.</summary>
      public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
      {
         /////////////////////////////////////////////////////////////////////////////////////////
         // The serializer never hands a null to this converter: it reads a null itself for a  //
         // member that may be null and refuses it for one that may not. Any token but text    //
         // makes GetString throw, which the caller treats as a value that does not fit.       //
         /////////////////////////////////////////////////////////////////////////////////////////
         string text = reader.GetString()!;
         bool namesItsOffset = text.EndsWith('Z') || (text.Length > 6 && text[^6] is '+' or '-' && text[^3] == ':');
         return namesItsOffset && reader.TryGetDateTimeOffset(out DateTimeOffset value)
            ? value.ToUniversalTime() : throw new JsonException("A date and time must be ISO 8601 with a trailing Z or an offset.");
      }



      /// <summary>Writes the instant in UTC with seven fractional digits and a trailing Z.</summary>
      public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
         => writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
   }



   /// <summary>
   /// What the serializer does with one of the envelope's types: nothing. It would read an envelope without any of the
   /// checks <see cref="ParseRequest"/> and <see cref="ParseReply"/> make (another version, a malformed operation name,
   /// a member the envelope does not have), and with options of a caller's choosing it could write one indented across
   /// lines, which the pipe's framing cannot carry. So both are closed, and the refusal names the functions to use
   /// instead. Each envelope type names this converter in an attribute on the type itself, which is why the refusal
   /// holds for the default options and for options built elsewhere, and not only for the two this contract builds.
   /// It does not hold against options that carry a converter written for the envelope type: the serializer asks the
   /// options before it asks the type.
   /// </summary>
   internal sealed class NotBySerializer<TEnvelope> : JsonConverter<TEnvelope>
   {
      /// <summary>Refuses to read.</summary>
      public override TEnvelope Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw Refusal();



      /// <summary>Refuses to write.</summary>
      public override void Write(Utf8JsonWriter writer, TEnvelope value, JsonSerializerOptions options) => throw Refusal();



      /// <summary>The refusal, naming the functions that read an envelope with its checks and the one that writes it.</summary>
      private static NotSupportedException Refusal()
         => new($"A {typeof(TEnvelope).Name} is neither read nor written by the serializer. Read a request with {nameof(ManagementJson)}.{nameof(ParseRequest)} and a reply with "
            + $"{nameof(ManagementJson)}.{nameof(ParseReply)}, which check the envelope, and write either with {nameof(ManagementJson)}.{nameof(ManagementJson.Write)}.");
   }
}
