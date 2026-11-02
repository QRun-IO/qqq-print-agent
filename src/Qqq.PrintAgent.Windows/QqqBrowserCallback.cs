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



   /// <summary>Opens the callback inbox on this user's per-session pipe name, so only this user's companion in this logon session can reach it.</summary>
   public QqqBrowserCallback() : this(PipeName()) { }



   /// <summary>
   /// Creates the one-instance pipe. It is current-user-only and the first instance wins, so a second sign-in attempt or a
   /// look-alike server created earlier by another process is refused rather than silently sharing the inbox.
   /// </summary>
   internal QqqBrowserCallback(string name) => _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
      PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);



   /// <summary>
   /// Waits for one forwarded callback address. The sender must be a live process in this user's session, the address must
   /// be exactly this application's redirect with a query, and only then is "received" acknowledged. It returns the validated
   /// address; the caller still has to match it to its own sign-in attempt.
   /// </summary>
   public async Task<Uri> ReceiveAsync(CancellationToken token)
   {
      await _pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
      using Process sender = ValidateClientSession(_pipe.SafePipeHandle);
      Uri callback = Validate(await AgentPipeFraming.ReadAsync(_pipe, MaximumLength, token).ConfigureAwait(false));
      await AgentPipeFraming.WriteResponseAsync(_pipe, "received", token).ConfigureAwait(false);
      return callback;
   }



   /// <summary>Forwards a browser callback from the protocol handler to the running companion, using the installed companion's path as the expected peer.</summary>
   public static Task ForwardAsync(string callback, CancellationToken token = default) => ForwardAsync(callback, PipeName(),
      InstalledCompanionPath(), token);



   /// <summary>
   /// Sends a callback address to the companion that is waiting for it. The address is validated before anything connects,
   /// the whole exchange has a five second limit, and the process serving the pipe must be the expected companion executable
   /// in this session, so the address (which carries a one-time code) is never handed to an impostor. The companion must acknowledge it.
   /// </summary>
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



   /// <summary>
   /// Accepts only an absolute address that is this application's exact redirect, has a query (the provider's code and
   /// state), no fragment, no control characters and a bounded length. Everything else is rejected as a forged or malformed callback.
   /// </summary>
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



   /// <summary>Derives the pipe name from the user's SID and logon session, so each signed-in user session has its own inbox and no name is shared across users.</summary>
   private static string PipeName()
   {
      using var identity = WindowsIdentity.GetCurrent();
      using var process = Process.GetCurrentProcess();
      string scope = identity.User?.Value + ":" + process.SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
      return "QqqPrintAgent.Auth." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
   }



   /// <summary>
   /// Verifies the process serving the pipe before a callback is sent: it must be in this session, still running, and the
   /// expected companion executable by full path. The returned process handle is held for the exchange so its id cannot be reused.
   /// </summary>
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



   /// <summary>Checks that the process that connected to the inbox is alive and in this user's session; a sender from another session is refused.</summary>
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



   /// <summary>Closes the inbox, which ends the sign-in attempt's ability to receive a callback.</summary>
   public void Dispose() => _pipe.Dispose();



   /// <summary>Returns the id of the process that created the server end of a named pipe.</summary>
   [DllImport("kernel32.dll", SetLastError = true)]
   [return: MarshalAs(UnmanagedType.Bool)]
   private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);



   /// <summary>Returns the id of the process at the client end of a named pipe.</summary>
   [DllImport("kernel32.dll", SetLastError = true)]
   [return: MarshalAs(UnmanagedType.Bool)]
   private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);



   /// <summary>Opens a process with limited query rights so its executable path can be read.</summary>
   [DllImport("kernel32.dll", SetLastError = true)]
   private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);



   /// <summary>Reads the full image path of a process, which is more reliable than the module list for this check.</summary>
   [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
   [return: MarshalAs(UnmanagedType.Bool)]
   private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, [Out] char[] name, ref uint size);
}
