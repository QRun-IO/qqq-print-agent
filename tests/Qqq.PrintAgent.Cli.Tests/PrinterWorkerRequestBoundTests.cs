/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The bound on the print helper's one request: at most 1 MiB characters, counted while the line is read, so a request
/// that never ends is refused after a bounded amount of reading and is never held in memory whole. The helper loop runs
/// inside the test process over readers that hand out text the way a pipe does.
/// </summary>
public sealed class PrinterWorkerRequestBoundTests
{
   /// <summary>A request of exactly the bound is read and carried out; one character more is refused and nothing is carried out.</summary>
   [Theory]
   [InlineData(0, true)]
   [InlineData(1, false)]
   public async Task RequestIsBoundedAtOneMebibyteOfCharacters(int over, bool accepted)
   {
      string request = Padded(PrinterWorkerHost.MaximumRequestCharacters + over);
      using StringReader input = new(request + "\n");

      (int exit, PrinterWorkerHost.Response response, CountingPrinters printers) = await RunAsync(input);

      Assert.Equal(1_048_576, PrinterWorkerHost.MaximumRequestCharacters);
      Assert.Equal(accepted ? 0 : 1, exit);
      Assert.Equal(accepted, response.Success);
      Assert.Equal(accepted ? null : "Invalid printer worker request size.", response.Error);
      Assert.Equal(accepted ? 1 : 0, printers.Discoveries);
   }



   /// <summary>
   /// A request that never ends is refused while it is being read: the helper stops asking for more once the bound is
   /// passed, having taken at most one more piece (4,096 characters) than a request may have. Before the bound was counted
   /// during reading, this input would have been read without end.
   /// </summary>
   [Fact]
   public async Task EndlessRequestIsRefusedWhileItIsRead()
   {
      using EndlessReader input = new();

      (int exit, PrinterWorkerHost.Response response, CountingPrinters printers) = await RunAsync(input);

      Assert.Equal(1, exit);
      Assert.Equal((false, "Invalid printer worker request size."), (response.Success, response.Error));
      Assert.InRange(input.Handed, PrinterWorkerHost.MaximumRequestCharacters + 1, PrinterWorkerHost.MaximumRequestCharacters + 4096);
      Assert.Equal(0, printers.Discoveries);
   }



   /// <summary>A request that arrives a character at a time, as it can from a pipe, is put together and carried out.</summary>
   [Fact]
   public async Task RequestArrivingInPiecesIsReadWhole()
   {
      using TricklingReader input = new("{\"operation\":\"discover\"}\n");
      (int exit, PrinterWorkerHost.Response response, CountingPrinters printers) = await RunAsync(input);
      Assert.Equal((0, true, 1), (exit, response.Success, printers.Discoveries));
      Assert.True(input.Reads >= 25);
   }



   /// <summary>
   /// The request is the first line, whichever way the line ends (line feed, carriage return and line feed, a bare carriage
   /// return, or the end of the input), and nothing after it is used: a helper carries out exactly one request. The last
   /// two cases are the ones that show a bare carriage return ends the line: were it only white space inside the line,
   /// the text after it would be part of the request, which would then not be one JSON value and would be refused.
   /// </summary>
   [Theory]
   [InlineData("\n")]
   [InlineData("\r\n")]
   [InlineData("\r")]
   [InlineData("")]
   [InlineData("\n{\"operation\":\"cancel\"}\n")]
   [InlineData("\nnot a request at all")]
   [InlineData("\r{\"operation\":\"cancel\"}\n")]
   [InlineData("\rnot a request at all")]
   public async Task RequestIsTheFirstLineHoweverItEnds(string ending)
   {
      using StringReader input = new("{\"operation\":\"discover\"}" + ending);
      (int exit, PrinterWorkerHost.Response response, CountingPrinters printers) = await RunAsync(input);
      Assert.Equal((0, true, 1), (exit, response.Success, printers.Discoveries));
   }



   /// <summary>An empty first line is a request with nothing in it, which is not JSON: it is answered with a failure, as before, and is not mistaken for input that ended.</summary>
   [Fact]
   public async Task EmptyFirstLineIsNotARequest()
   {
      using StringReader input = new("\n{\"operation\":\"discover\"}\n");
      (int exit, PrinterWorkerHost.Response response, CountingPrinters printers) = await RunAsync(input);
      Assert.Equal((1, false, 0), (exit, response.Success, printers.Discoveries));
      Assert.NotEqual("Invalid printer worker request size.", response.Error, StringComparer.Ordinal);
   }



   /// <summary>Runs the helper loop over the given input with a backend that only counts, and returns the exit code, the single answer and the backend.</summary>
   private static async Task<(int Exit, PrinterWorkerHost.Response Response, CountingPrinters Printers)> RunAsync(TextReader input)
   {
      CountingPrinters printers = new();
      using StringWriter output = new();
      int exit = await PrinterWorkerHost.RunAsync(printers, new FakeSpoolerJobs(), new FakeParentWatch(), "4242", input, output);
      string line = Assert.Single(output.ToString().Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries));
      return (exit, Assert.IsType<PrinterWorkerHost.Response>(JsonSerializer.Deserialize<PrinterWorkerHost.Response>(line, PrintApiClient.JsonOptions)), printers);
   }



   /// <summary>A discover request padded with an ignored member to exactly the given number of characters.</summary>
   private static string Padded(int length)
   {
      const string start = "{\"operation\":\"discover\",\"jobId\":\"";
      const string end = "\"}";
      return start + new string('a', length - start.Length - end.Length) + end;
   }



   /// <summary>A printer backend with no printers that counts how often it was asked for its inventory.</summary>
   private sealed class CountingPrinters : IPrinterBackend
   {
      /// <summary>How often discovery was asked for.</summary>
      internal int Discoveries { get; private set; }



      /// <summary>Counts the call and returns an empty inventory.</summary>
      public IReadOnlyList<PrinterInfo> Discover()
      {
         Discoveries++;
         return [];
      }



      /// <summary>Never reached by these tests; it has nothing to print on.</summary>
      public Task<string> PrintFileAsync(PrintJob job, string path, TimeSpan submissionTimeout, CancellationToken cancellationToken)
         => throw new InvalidOperationException("This backend has no printer.");
   }



   /// <summary>Input that never ends and never has a line end: every read is filled with the letter x. It counts what it handed out.</summary>
   private sealed class EndlessReader : TextReader
   {
      /// <summary>How many characters were handed out so far.</summary>
      internal long Handed { get; private set; }



      /// <summary>Fills the whole request with the letter x.</summary>
      public override int Read(char[] buffer, int index, int count)
      {
         Array.Fill(buffer, 'x', index, count);
         Handed += count;
         return count;
      }
   }



   /// <summary>Input that hands out one character for each read, however many were asked for, and counts the reads.</summary>
   private sealed class TricklingReader(string text) : TextReader
   {
      private int _position;



      /// <summary>How many reads were made.</summary>
      internal int Reads { get; private set; }



      /// <summary>Hands out the next character, or nothing at the end.</summary>
      public override int Read(char[] buffer, int index, int count)
      {
         Reads++;
         if(_position == text.Length || count == 0)
         {
            return 0;
         }
         buffer[index] = text[_position++];
         return 1;
      }
   }
}
