/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Qqq.PrintAgent.Windows;

/// <summary>A callback inbox exists only during an explicit sign-in attempt. It never contains a PKCE verifier or session cookie.</summary>
public sealed partial class QqqBrowserCallback : IDisposable
{
   public const string RedirectUri = "io.qrun.qqq-print-agent:/oauth/callback";
   private const int MaximumLength = 8192;
   private readonly NamedPipeServerStream _pipe;

   public QqqBrowserCallback() : this(PipeName()) { }

   internal QqqBrowserCallback(string name) => _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
      PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);

   public async Task<Uri> ReceiveAsync(CancellationToken token)
   {
      await _pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
      using Process sender = ValidateClientSession(_pipe.SafePipeHandle);
      Uri callback = Validate(await AgentPipeFraming.ReadAsync(_pipe, MaximumLength, token).ConfigureAwait(false));
      await AgentPipeFraming.WriteResponseAsync(_pipe, "received", token).ConfigureAwait(false);
      return callback;
   }

   public static Task ForwardAsync(string callback, CancellationToken token = default) => ForwardAsync(callback, PipeName(),
      InstalledCompanionPath(), token);

   internal static async Task ForwardAsync(string callback, string pipeName, string expectedExecutable, CancellationToken token)
   {
      _ = Validate(callback);
      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
      timeout.CancelAfter(TimeSpan.FromSeconds(5));
      await using NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Anonymous);
      await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
      using Process peer = AuthenticatePeer(pipe.SafePipeHandle, expectedExecutable);
      await AgentPipeFraming.WriteAsync(pipe, callback, MaximumLength, timeout.Token).ConfigureAwait(false);
      string acknowledgement = await AgentPipeFraming.ReadAsync(pipe, 32, timeout.Token).ConfigureAwait(false);
      if(!string.Equals(acknowledgement, "received", StringComparison.Ordinal))
      {
         throw new InvalidDataException("The active sign-in attempt did not accept the callback.");
      }
   }

   internal static Uri Validate(string value)
   {
      if(value.Length > MaximumLength || value.Any(char.IsControl) || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
         || !string.Equals(uri.GetLeftPart(UriPartial.Path), RedirectUri, StringComparison.Ordinal)
         || uri.Fragment.Length != 0 || uri.Query.Length == 0)
      {
         throw new InvalidDataException("Invalid QQQ browser callback.");
      }
      return uri;
   }

   private static string PipeName()
   {
      using var identity = WindowsIdentity.GetCurrent();
      using var process = Process.GetCurrentProcess();
      string scope = identity.User?.Value + ":" + process.SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
      return "QqqPrintAgent.Auth." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
   }

   private static Process AuthenticatePeer(SafePipeHandle pipe, string expectedExecutable)
   {
      if(!GetNamedPipeServerProcessId(pipe, out uint id))
      {
         throw new UnauthorizedAccessException("Cannot verify the active sign-in companion.");
      }
      var process = Process.GetProcessById(checked((int)id));
      using var current = Process.GetCurrentProcess();
      using SafeProcessHandle handle = OpenProcess(0x1000, false, id);
      char[] name = new char[32768];
      uint length = 32768;
      if(handle.IsInvalid || !QueryFullProcessImageName(handle, 0, name, ref length) || process.SessionId != current.SessionId
         || !string.Equals(new string(name, 0, checked((int)length)), expectedExecutable, StringComparison.OrdinalIgnoreCase) || process.HasExited)
      {
         process.Dispose();
         throw new UnauthorizedAccessException("The callback endpoint is not the installed companion in this user session.");
      }
      return process;
   }

   private static Process ValidateClientSession(SafePipeHandle pipe)
   {
      if(!GetNamedPipeClientProcessId(pipe, out uint id))
      {
         throw new UnauthorizedAccessException("Cannot verify the callback sender.");
      }
      var process = Process.GetProcessById(checked((int)id));
      using var current = Process.GetCurrentProcess();
      if(process.SessionId != current.SessionId || process.HasExited)
      {
         process.Dispose();
         throw new UnauthorizedAccessException("The callback sender is outside this user session.");
      }
      return process;
   }

   public void Dispose() => _pipe.Dispose();

   [DllImport("kernel32.dll", SetLastError = true)]
   [return: MarshalAs(UnmanagedType.Bool)]
   private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

   [DllImport("kernel32.dll", SetLastError = true)]
   [return: MarshalAs(UnmanagedType.Bool)]
   private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

   [DllImport("kernel32.dll", SetLastError = true)]
   private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);

   [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
   [return: MarshalAs(UnmanagedType.Bool)]
   private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, [Out] char[] name, ref uint size);
}
