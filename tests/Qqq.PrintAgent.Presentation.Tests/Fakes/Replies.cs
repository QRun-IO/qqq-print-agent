/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Tests.Fakes;

/// <summary>
/// Replies for <see cref="FakePipe"/>, each as the contract's own parser reads it from the one line the pipe carries:
/// built from the contract's records, or taken from the contract's golden files, which this project links from the
/// Windows layer's suite. A test therefore meets a reply in the form the real pipe client would hand over.
/// </summary>
internal static class Replies
{
   /// <summary>The message the golden failures carry, and the default of the failures built here.</summary>
   internal const string Message = "Synthetic message for people.";



   /// <summary>Where the golden files are copied to beside the test assembly.</summary>
   private static string GoldenDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "management");



   /// <summary>A success carrying a result record.</summary>
   internal static ManagementReplyParse Success<TResult>(TResult result)
      where TResult : class
      => Text(ManagementJson.Write(ManagementReply.Success(result)));



   /// <summary>A failure with a code that carries no details.</summary>
   internal static ManagementReplyParse Failure(string code, string message = Message) => Text(ManagementJson.Write(ManagementReply.Failure(code, message)));



   /// <summary>A failure with one of the codes that carry details.</summary>
   internal static ManagementReplyParse Failure<TDetails>(string code, string message, TDetails details)
      where TDetails : class
      => Text(ManagementJson.Write(ManagementReply.Failure(code, message, details)));



   /// <summary>What the parser makes of reply text, whatever it is.</summary>
   internal static ManagementReplyParse Text(string? wire) => ManagementJson.ParseReply(wire);



   /// <summary>A golden file's reply.</summary>
   internal static ManagementReplyParse Golden(string fileName) => Text(GoldenText(fileName));



   /// <summary>A golden file's text with its indentation removed and nothing else changed, which is the one line the wire carries.</summary>
   internal static string GoldenText(string fileName)
   {
      using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(GoldenDirectory, fileName)));
      return JsonSerializer.Serialize(document.RootElement);
   }
}
