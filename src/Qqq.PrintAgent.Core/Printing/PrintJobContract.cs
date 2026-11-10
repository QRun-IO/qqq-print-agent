/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// Reads jobs the server sends and refuses any that break the contract, so nothing past the API client ever sees a job
/// with a missing member or an identifier that is unsafe to put in a route, a file name or a log line. Whether a
/// well-formed job may print (format supported, size and copies within policy, media still current) stays with the
/// worker, which reports those as FAILED; this is only about shape. Fields the agent does not read are ignored
/// wherever they appear, as the contract requires, because the server relays a submitter's options unchanged.
/// </summary>
internal static class PrintJobContract
{
   /// <summary>The most queued jobs one response may list, as the contract states.</summary>
   internal const int QueuedLimit = 200;

   private const int TitleLimit = 1024;
   private const int TokenLimit = 32;
   private const int TimestampLimit = 64;
   private const int CopiesLimit = 32767;

   /// <summary>The members of a job the agent reads, by their contract names. A test holds this list to the <see cref="PrintJob"/> record.</summary>
   internal static readonly string[] JobMembers = ["jobId", "printerId", "title", "format", "contentSha256", "contentLength", "options", "createdAt", "status"];

   /// <summary>The members of a job's options the agent reads. A test holds this list to the <see cref="PrintOptions"/> record.</summary>
   internal static readonly string[] OptionMembers = ["copies", "scale", "paperWidthMm", "paperHeightMm", "mediaProfile"];

   /// <summary>The members of a media selection the agent reads. A test holds this list to the <see cref="PrinterMediaSelection"/> record.</summary>
   internal static readonly string[] MediaMembers = ["id", "revision"];

   private static readonly string[] Identifiers = ["jobId", "printerId"];



   /// <summary>
   /// Reads a claim response: null when the server has no job for this device, otherwise the validated job. A response
   /// that is not a JSON object, or a job that breaks the contract, throws. Whenever the job still names itself with one
   /// usable identifier the failure carries a stand-in for it, so the worker can answer for the job instead of being
   /// offered it again forever; only a response that names no job the agent can answer for throws without one.
   /// </summary>
   internal static PrintJob? ReadClaim(ReadOnlyMemory<byte> body)
   {
      using JsonDocument document = ProtocolJson.ParseObject(ProtocolJson.WithoutByteOrderMark(body), "claim response", ProtocolJson.ReaderDepth);
      JsonElement[] offered = ProtocolJson.Members(document.RootElement, "job");
      if(offered.Length > 1)
      { throw new PrintingProtocolException("The claim response names more than one job."); }
      if(offered.Length == 0 || offered[0].ValueKind == JsonValueKind.Null)
      { return null; }
      JsonElement element = offered[0];
      if(element.ValueKind != JsonValueKind.Object)
      { throw new PrintingProtocolException("The claimed job is not a JSON object."); }

      /////////////////////////////////////////////////////////////////////////
      // the stand-in is built first, from the members as the server spelled //
      // them, so every refusal below can name the job it is about           //
      /////////////////////////////////////////////////////////////////////////
      PrintJob? named = StandIn(Text(element, "jobId"), Text(element, "printerId"), Text(element, "title"), Text(element, "format"), Text(element, "createdAt"));
      if(RepeatsReadMember(element))
      { throw Violation("The claimed job repeats a member the agent reads.", named); }
      PrintJob job;
      try
      { job = ProtocolJson.Read<PrintJob>(element, "claimed job"); }
      catch(PrintingProtocolException violation)
      { throw Violation(violation.Message, named); }
      return ClaimProblem(job) is { } problem
         ? throw Violation(problem, StandIn(job.JobId, job.PrinterId, job.Title, job.Format, job.CreatedAt))
         : Normalize(job);
   }



   /// <summary>
   /// Reads the list of jobs the server is holding for this device. They are shown, never printed from here, so only
   /// what the display relies on is required of each: a usable job and printer identifier, each given once. Everything
   /// else that is missing becomes empty text, and a repeated display member is tolerated because nothing is decided
   /// from it; the job is judged in full when it is claimed. A response without a list means none; more than the
   /// contract's limit is refused.
   /// </summary>
   internal static IReadOnlyList<PrintJob> ReadQueued(ReadOnlyMemory<byte> body)
   {
      using JsonDocument document = ProtocolJson.ParseObject(ProtocolJson.WithoutByteOrderMark(body), "queued jobs response", ProtocolJson.ReaderDepth);
      JsonElement[] lists = ProtocolJson.Members(document.RootElement, "jobs");
      if(lists.Length > 1)
      { throw new PrintingProtocolException("The queued jobs response holds more than one list."); }
      if(lists.Length == 0 || lists[0].ValueKind == JsonValueKind.Null)
      { return []; }
      if(lists[0].ValueKind != JsonValueKind.Array || lists[0].GetArrayLength() > QueuedLimit)
      { throw new PrintingProtocolException($"The queued jobs response does not hold a list of at most {QueuedLimit} jobs."); }
      List<PrintJob> jobs = [];
      foreach(JsonElement element in lists[0].EnumerateArray())
      {
         if(element.ValueKind != JsonValueKind.Object)
         { throw new PrintingProtocolException("A queued job is not a JSON object."); }
         PrintJob job = ProtocolJson.Read<PrintJob>(element, "queued job");
         if(!PrintApiClient.ValidControlId(job.JobId) || !PrintApiClient.ValidControlId(job.PrinterId) || ProtocolJson.RepeatsMember(element, Identifiers))
         { throw new PrintingProtocolException("A queued job has no usable job or printer identifier."); }
         jobs.Add(Normalize(job) with { Title = job.Title ?? "", Format = job.Format ?? "", ContentSha256 = job.ContentSha256 ?? "", Options = job.Options ?? new PrintOptions() });
      }
      return jobs;
   }



   /// <summary>
   /// True when a claimed job gives a member the agent reads more than once: in the job itself, in its options or in
   /// its media selection. The agent would act on one of the values and the server may have meant the other, so such a
   /// job is refused rather than printed on a guess. Fields the agent does not read are not looked at.
   /// </summary>
   private static bool RepeatsReadMember(JsonElement job)
   {
      if(ProtocolJson.RepeatsMember(job, JobMembers))
      { return true; }
      if(ProtocolJson.Members(job, "options") is not [{ ValueKind: JsonValueKind.Object } options])
      { return false; }
      return ProtocolJson.RepeatsMember(options, OptionMembers)
         || (ProtocolJson.Members(options, "mediaProfile") is [{ ValueKind: JsonValueKind.Object } media] && ProtocolJson.RepeatsMember(media, MediaMembers));
   }



   /// <summary>
   /// Says what is wrong with a claimed job, or null when its shape is acceptable: safe identifiers, a bounded title, a
   /// format token, a SHA-256 hash, a positive length and usable options.
   /// </summary>
   private static string? ClaimProblem(PrintJob job)
   {
      if(!PrintApiClient.ValidControlId(job.JobId))
      { return "The claimed job has no usable job identifier."; }
      if(!PrintApiClient.ValidControlId(job.PrinterId))
      { return "The claimed job has no usable printer identifier."; }
      if(job.Title is null || !WithinTitleLimit(job.Title))
      { return "The claimed job has no title, or one that is too long."; }
      if(!IsToken(job.Format))
      { return "The claimed job has no usable document format."; }
      if(job.ContentSha256 is not { Length: 64 } || !job.ContentSha256.All(char.IsAsciiHexDigit))
      { return "The claimed job has no SHA-256 content hash."; }
      if(job.ContentLength is < 1 or > int.MaxValue)
      { return "The claimed job declares an empty or impossibly large document."; }
      return OptionsProblem(job.Options);
   }



   /// <summary>
   /// Says what is wrong with a job's options, or null when they are usable: present, a possible copy count, a scale
   /// token, and paper dimensions that are complete, in range and not combined with a media profile. Whether a selected
   /// media profile is well formed and still current is judged once, by <see cref="PrinterMediaProfile.RequireMatch"/>,
   /// just before submission.
   /// </summary>
   private static string? OptionsProblem(PrintOptions? options)
   {
      if(options is null)
      { return "The claimed job has no print options."; }
      if(options.Copies is < 1 or > CopiesLimit)
      { return "The claimed job asks for an impossible number of copies."; }
      if(!IsToken(options.Scale))
      { return "The claimed job has no usable scale option."; }
      try
      { _ = options.CustomPaperMicrons(); }
      catch(InvalidDataException)
      { return "The claimed job has incomplete or out-of-range paper dimensions, or combines them with a media profile."; }
      return null;
   }



   /// <summary>
   /// True for a title within the contract's limit. The contract counts Unicode code points, as JSON Schema does, so a
   /// title is measured that way and not in UTF-16 units: 1,024 characters from outside the basic plane, which take two
   /// units each, are as acceptable as 1,024 letters.
   /// </summary>
   private static bool WithinTitleLimit(string title) => title.EnumerateRunes().Count() <= TitleLimit;



   /// <summary>True for a short token of ASCII letters, digits and underscores, the form a format or scale name takes.</summary>
   private static bool IsToken([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value) => value is { Length: >= 1 and <= TokenLimit } && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');



   /// <summary>Replaces a missing or over-long creation time or status, which are only ever displayed, with empty text.</summary>
   private static PrintJob Normalize(PrintJob job) => job with
   {
      CreatedAt = job.CreatedAt is { Length: <= TimestampLimit } created ? created : "",
      Status = job.Status is { Length: <= TokenLimit } status ? status : "",
   };



   /// <summary>
   /// Builds the stand-in for a malformed job: its identifier, when that is usable, and whichever display facts are safe
   /// to keep, with the rest left empty. Without a usable identifier there is nothing to report against, so there is no
   /// stand-in.
   /// </summary>
   private static PrintJob? StandIn(string? jobId, string? printerId, string? title, string? format, string? createdAt)
   {
      if(!PrintApiClient.ValidControlId(jobId))
      { return null; }
      return new PrintJob(jobId, PrintApiClient.ValidControlId(printerId) ? printerId : "", title is not null && WithinTitleLimit(title) ? title : "", IsToken(format) ? format : "",
         "", 0, new PrintOptions(), createdAt is { Length: <= TimestampLimit } ? createdAt : "", PrintJobState.Claimed);
   }



   /// <summary>Reads the text member the contract names, or null when it is missing, not text, or given more than once so that it is not clear which value is meant.</summary>
   private static string? Text(JsonElement element, string name)
      => ProtocolJson.Members(element, name) is [{ ValueKind: JsonValueKind.String } member] ? member.GetString() : null;



   /// <summary>The failure for a malformed claimed job, carrying the stand-in when the job could be identified.</summary>
   private static PrintingProtocolException Violation(string message, PrintJob? standIn)
      => standIn is null ? new PrintingProtocolException(message) : new PrintingProtocolException(message, standIn);
}
