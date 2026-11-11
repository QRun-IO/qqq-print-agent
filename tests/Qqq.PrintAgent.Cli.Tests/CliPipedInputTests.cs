/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// How the bytes of redirected standard input become the enrollment code. They are read as bytes and decoded as UTF-8,
/// not in the console input code page, and a byte-order mark at the very start says what the text is and is not part of
/// it. That matters because Windows PowerShell 5.1 puts a UTF-8 mark in front of piped text whenever its output encoding
/// is UTF-8, whatever the console code page: read through the console's text reader on a code page other than UTF-8,
/// those three bytes arrived as three other characters in front of the code and the server rejected it as unknown.
/// Every input here is given as the bytes a shell or a file would deliver, and the code is what the fake service is sent.
/// </summary>
public sealed class CliPipedInputTests
{
   private const string Code = "synthetic-code-4471";
   private const string Server = "https://qqq.example.test";



   /// <summary>
   /// The piped code reaches the service exactly, whatever is in front of it. In order: plain text as cmd pipes it; with
   /// no line end, as a file may be; with a bare line feed; behind one UTF-8 mark (Windows PowerShell 5.1 with a UTF-8
   /// output encoding, on any console code page); behind two (the same with a UTF-8 console code page as well); as
   /// UTF-16 behind its mark, little-endian (a file Windows PowerShell 5.1 wrote with a redirection) and big-endian;
   /// and with only its first line used when more follow. Nothing is prompted, no key is read, standard error stays
   /// empty and the code is not printed.
   /// </summary>
   [Theory]
   [InlineData("", "utf-8", "synthetic-code-4471\r\n")]
   [InlineData("", "utf-8", "synthetic-code-4471")]
   [InlineData("", "utf-8", "synthetic-code-4471\n")]
   [InlineData("EF BB BF", "utf-8", "synthetic-code-4471\r\n")]
   [InlineData("EF BB BF", "utf-8", "synthetic-code-4471")]
   [InlineData("EF BB BF EF BB BF", "utf-8", "synthetic-code-4471\r\n")]
   [InlineData("FF FE", "utf-16", "synthetic-code-4471\r\n")]
   [InlineData("FF FE", "utf-16", "synthetic-code-4471")]
   [InlineData("FE FF", "utf-16BE", "synthetic-code-4471\r\n")]
   [InlineData("", "utf-8", "synthetic-code-4471\r\nsynthetic-second-line\r\n")]
   [InlineData("EF BB BF", "utf-8", " synthetic-code-4471 \r\nsynthetic-second-line\r\n")]
   public async Task PipedCode_ReachesTheServiceWhateverMarkIsInFrontOfIt(string mark, string encoding, string text)
   {
      using CliFixture cli = new() { Reply = "Enrolled.", Input = Bytes(mark, encoding, text) };

      int exitCode = await cli.RunAsync("enroll", "--server", Server);

      Assert.Equal(0, exitCode);
      Assert.Equal(new PipeRequest("enroll", Server, Code), Assert.Single(cli.Sent));
      Assert.Equal(1, cli.LinesRead);
      Assert.Equal(0, cli.KeysRead);
      Assert.Equal("Enrolled.\n", cli.Output);
      Assert.Equal("", cli.Error);
   }



   /// <summary>
   /// Text outside ASCII is decoded as UTF-8, with or without a mark, and as UTF-16 behind a UTF-16 mark: letters with
   /// accents, Cyrillic, a character of three bytes and one from outside the basic plane, which takes four bytes in
   /// UTF-8 and a surrogate pair in UTF-16. A real code is ASCII; this shows that the bytes are decoded by their
   /// encoding and not in whatever code page the console has, under which each of these bytes would be another letter.
   /// </summary>
   [Theory]
   [InlineData("", "utf-8")]
   [InlineData("EF BB BF", "utf-8")]
   [InlineData("FF FE", "utf-16")]
   [InlineData("FE FF", "utf-16BE")]
   public async Task PipedTextOutsideAscii_IsDecodedByItsEncodingNotByTheConsoleCodePage(string mark, string encoding)
   {
      string text = "synthétique-код-符-" + char.ConvertFromUtf32(0x1F5A8) + "-4471";
      using CliFixture cli = new() { Input = Bytes(mark, encoding, text + "\r\n") };

      int exitCode = await cli.RunAsync("enroll", "--server", Server);

      Assert.Equal(0, exitCode);
      Assert.Equal(new PipeRequest("enroll", Server, text), Assert.Single(cli.Sent));
      Assert.Equal("", cli.Error);
   }



   /// <summary>
   /// Input that holds a mark and no code holds no code: nothing is sent, and the one line on standard error says a code
   /// is required. In order: nothing at all; a UTF-8 mark and nothing else; the mark and a line end, which is what
   /// Windows PowerShell 5.1 pipes for an empty string; the mark and spaces; two marks; a UTF-16 mark alone, with a
   /// line end and with a space; white space from outside ASCII (an ideographic space and a no-break space), plain and
   /// behind a mark; and a blank first line, because one line is read and a code on the second is not looked for.
   /// </summary>
   [Theory]
   [InlineData("", "utf-8", "")]
   [InlineData("", "utf-8", "\r\n")]
   [InlineData("EF BB BF", "utf-8", "")]
   [InlineData("EF BB BF", "utf-8", "\r\n")]
   [InlineData("EF BB BF", "utf-8", "  \r\n")]
   [InlineData("EF BB BF EF BB BF", "utf-8", "\r\n")]
   [InlineData("FF FE", "utf-16", "")]
   [InlineData("FF FE", "utf-16", "\r\n")]
   [InlineData("FF FE", "utf-16", " \r\n")]
   [InlineData("FE FF", "utf-16BE", "\r\n")]
   [InlineData("", "utf-8", "　 \r\n")]
   [InlineData("EF BB BF", "utf-8", "　 \r\n")]
   [InlineData("", "utf-8", "\r\nsynthetic-code-4471\r\n")]
   public async Task PipedInputWithNoCode_FailsWithoutContactingTheService(string mark, string encoding, string text)
   {
      using CliFixture cli = new() { Input = Bytes(mark, encoding, text) };

      int exitCode = await cli.RunAsync("enroll", "--server", Server);

      Assert.Equal(1, exitCode);
      Assert.Empty(cli.Sent);
      Assert.Equal(1, cli.LinesRead);
      Assert.Equal(0, cli.KeysRead);
      Assert.Equal("", cli.Output);
      Assert.Equal("Enrollment code is required.\n", cli.Error);
   }



   /// <summary>
   /// What is not repaired, stated so that nobody takes it for an accident. A byte that is not UTF-8 becomes the
   /// replacement character. UTF-16 text with no mark in front (what "cmd /u" pipes) is read as UTF-8, so every other
   /// character is a null. A UTF-16 mark that is not at the very start (Windows PowerShell 5.1 with a UTF-8 console
   /// code page and a UTF-16 output encoding sends it behind a UTF-8 mark) is not a mark. In each case what is sent is
   /// not the code, and the remedy is the variable or a UTF-8 pipe. Nothing is printed but the service's reply. In the expected text "(bad)" stands for the
   /// replacement character and "(null)" for a null.
   /// </summary>
   [Theory]
   [InlineData("73 79 6E FF 2D 34 34 37 31 0D 0A", "syn(bad)-4471")]
   [InlineData("73 00 79 00 6E 00 0D 00 0A 00", "s(null)y(null)n(null)")]
   [InlineData("EF BB BF FF FE 73 00 79 00 6E 00 0D 00 0A 00", "(bad)(bad)s(null)y(null)n(null)")]
   public async Task PipedBytesThatAreNotUtf8AndCarryNoMarkAtTheStart_AreNotRepaired(string bytes, string expected)
   {
      string sent = expected.Replace("(bad)", char.ConvertFromUtf32(0xFFFD), StringComparison.Ordinal).Replace("(null)", char.ConvertFromUtf32(0), StringComparison.Ordinal);
      using CliFixture cli = new() { Input = Convert.FromHexString(bytes.Replace(" ", "", StringComparison.Ordinal)) };

      int exitCode = await cli.RunAsync("enroll", "--server", Server);

      Assert.Equal(0, exitCode);
      Assert.Equal(new PipeRequest("enroll", Server, sent), Assert.Single(cli.Sent));
      Assert.Equal("Synthetic service reply.\n", cli.Everything);
   }



   /// <summary>
   /// The code is read from the bytes and from nowhere else: with the code in the variable, redirected standard input
   /// is never opened, and at a console it is not opened either, because the code is typed.
   /// </summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task RedirectedInput_IsOpenedOnlyWhenTheCodeIsPipedIn(bool variable)
   {
      using CliFixture cli = new() { Input = Bytes("EF BB BF", "utf-8", "synthetic-piped-code\r\n"), InputRedirected = variable };
      if(variable)
      {
         cli.Variables["QQQ_PRINT_ENROLLMENT_CODE"] = Code;
      }
      else
      {
         cli.Type(Code);
         cli.Press(ConsoleKey.Enter, '\r');
      }

      int exitCode = await cli.RunAsync("enroll", "--server", Server);

      Assert.Equal(0, exitCode);
      Assert.Equal(0, cli.LinesRead);
      Assert.Equal(new PipeRequest("enroll", Server, Code), Assert.Single(cli.Sent));
   }



   /// <summary>The bytes a source delivers, as <see cref="CliFixture.Bytes"/> builds them.</summary>
   private static byte[] Bytes(string mark, string encoding, string text) => CliFixture.Bytes(mark, encoding, text);
}
