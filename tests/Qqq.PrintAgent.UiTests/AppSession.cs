/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Security.Principal;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Patterns;
using FlaUI.Core.Tools;
using FlaUI.UIA3;


namespace Qqq.PrintAgent.UiTests;

/// <summary>
/// One launched app plus the UIA3 automation that drives it. Each session gets a fresh
/// temp directory passed as the app's data-folder variable, so presentation settings
/// are isolated. Activity logs intentionally use the shared protected machine directory.
/// Unless a test asks for the
/// splash, the session pre-writes settings that skip it. The companion requires an
/// elevated interactive test runner to access its protected administration UI.
/// </summary>
public sealed class AppSession : IDisposable
{
   private static readonly TimeSpan s_defaultTimeout = TimeSpan.FromSeconds(10);
   private static readonly TimeSpan s_launchTimeout = TimeSpan.FromSeconds(30);
   private static readonly TimeSpan s_pollInterval = TimeSpan.FromMilliseconds(250);



   /// <summary>Launches the app against a fresh temporary data folder and waits for its main window.</summary>
   public AppSession(bool showSplash = false)
   {
      using var identity = WindowsIdentity.GetCurrent();
      WindowsPrincipal principal = new(identity);
      if(!Environment.UserInteractive || !principal.IsInRole(WindowsBuiltInRole.Administrator))
      {
         throw new InvalidOperationException("Print Agent UI tests require an elevated interactive Windows desktop. Run the test command from an administrator terminal; the fixture does not automate UAC prompts.");
      }
      ExePath = ResolveExePath();
      DataDirectory = Path.Combine(Path.GetTempPath(), AppIdentity.Info.Id + "-uitests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(DataDirectory);
      File.WriteAllText(
          Path.Combine(DataDirectory, "settings.json"),
          $$"""{ "ShowSplashScreen": {{(showSplash ? "true" : "false")}}, "SplashMilliseconds": {{(showSplash ? 15000 : 2500)}}, "CheckForUpdatesOnStartup": false }""");

      var startInfo = new ProcessStartInfo(ExePath) { WorkingDirectory = Path.GetDirectoryName(ExePath) ?? "" };
      startInfo.Environment[AppIdentity.Info.DataDirectoryVariable] = DataDirectory;

      Automation = new UIA3Automation();
      try
      {
         App = Application.Launch(startInfo);
         MainWindow = WaitForMainWindow();
      }
      catch
      {
         Dispose();
         throw;
      }
   }



   public string ExePath { get; }

   public string DataDirectory { get; }

   public UIA3Automation Automation { get; }

   public Application App { get; }

   public Window MainWindow { get; }

   /// <summary>Requires the same live native window and process, including while its UIA provider is hidden.</summary>
   public void WaitForWindowVisibility(nint window, bool visible)
   {
      bool matched = Retry.WhileFalse(
         () => !App.HasExited && NativeWindowState.Matches(window, App.ProcessId, visible),
         timeout: s_defaultTimeout, interval: s_pollInterval).Success;
      if(!matched)
      {
         throw new TimeoutException($"The original HWND did not remain alive and become {(visible ? "visible" : "hidden")}. App exited: {App.HasExited}.");
      }
   }

   /// <summary>Launches the same executable and presentation profile to exercise instance forwarding.</summary>
   public Process Relaunch()
   {
      var startInfo = new ProcessStartInfo(ExePath) { WorkingDirectory = Path.GetDirectoryName(ExePath) ?? "", UseShellExecute = false };
      startInfo.Environment[AppIdentity.Info.DataDirectoryVariable] = DataDirectory;
      return Process.Start(startInfo) ?? throw new InvalidOperationException("The second companion process could not be started.");
   }



   /// <summary>The settings file as the app last saved it.</summary>
   public string ReadSettings() => ReadShared(Path.Combine(DataDirectory, "settings.json"));



   /// <summary>
   /// Finds the first match in the main window or any other top-level window of the
   /// app's process (WinUI menu flyouts and secondary windows are separate HWNDs).
   /// </summary>
   public AutomationElement? FindInApp(Func<ConditionFactory, ConditionBase> condition)
       => MainWindow.FindFirstDescendant(condition)
           ?? TopLevelWindows().Where(w => !w.Equals(MainWindow)).Select(w => w.FindFirstDescendant(condition)).FirstOrDefault(found => found is not null);



   /// <summary>Top-level windows owned by the app's process.</summary>
   public AutomationElement[] TopLevelWindows()
       => Automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(App.ProcessId));



   /// <summary>Retries the lookup until it returns an element, or fails naming what was awaited.</summary>
   public AutomationElement WaitFor(Func<AutomationElement?> lookup, string description, TimeSpan? timeout = null)
   {
      TimeSpan effective = timeout ?? s_defaultTimeout;
      RetryResult<AutomationElement?> result = Retry.WhileNull(lookup, timeout: effective, interval: s_pollInterval, ignoreException: true);
      return result.Result
          ?? throw new TimeoutException($"Timed out after {effective.TotalSeconds:0}s waiting for {description}. (app exited: {App.HasExited})");
   }



   /// <summary>Retries until the lookup returns nothing, or fails naming what was expected to disappear.</summary>
   public static void WaitUntilGone(Func<AutomationElement?> lookup, string description, TimeSpan? timeout = null)
   {
      TimeSpan effective = timeout ?? s_defaultTimeout;
      if(!Retry.WhileTrue(() => lookup() is not null, timeout: effective, interval: s_pollInterval, ignoreException: true).Success)
      {
         throw new TimeoutException($"Timed out after {effective.TotalSeconds:0}s waiting for {description} to go away.");
      }
   }



   /// <summary>Waits for the element with the given automation id in any of the app&apos;s windows.</summary>
   public AutomationElement WaitForElement(string automationId, TimeSpan? timeout = null)
       => WaitFor(() => FindInApp(cf => cf.ByAutomationId(automationId)), $"element '{automationId}'", timeout);



   /// <summary>Waits until no element with the given automation id remains.</summary>
   public void WaitForElementGone(string automationId, TimeSpan? timeout = null)
       => WaitUntilGone(() => FindInApp(cf => cf.ByAutomationId(automationId)), $"element '{automationId}'", timeout);



   /// <summary>
   /// Opens the top-level menu named <paramref name="menuName"/> and invokes the item
   /// <paramref name="itemAutomationId"/>. A freshly started app can swallow the first
   /// expand (its flyout opens before the menu is ready), so the menu is re-opened
   /// until the item appears.
   /// </summary>
   public void InvokeMenuItem(string menuName, string itemAutomationId)
   {
      AutomationElement menu = WaitFor(
          () => MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.MenuItem).And(cf.ByName(menuName))),
          $"menu '{menuName}'");
      AutomationElement? item = null;
      for(int attempt = 0; attempt < 4 && item is null; attempt++)
      {
         if(menu.Patterns.ExpandCollapse.TryGetPattern(out IExpandCollapsePattern? expandCollapse))
         {
            expandCollapse.Expand();
         }
         else
         {
            Invoke(menu);
         }
         Wait.UntilInputIsProcessed();
         item = Retry.WhileNull(() => FindInApp(cf => cf.ByAutomationId(itemAutomationId)), timeout: TimeSpan.FromSeconds(3), interval: s_pollInterval, ignoreException: true).Result;
      }
      Invoke(item ?? throw new TimeoutException($"Menu item '{itemAutomationId}' never appeared under '{menuName}'. (app exited: {App.HasExited})"));
   }



   /// <summary>Invokes the button named <paramref name="name"/> (XAML buttons only, not caption buttons).</summary>
   public void ClickButton(string name)
       => Invoke(WaitFor(
           () => FindInApp(cf => cf.ByControlType(ControlType.Button).And(cf.ByName(name)).And(cf.ByClassName("Button"))),
           $"button '{name}'"));



   /// <summary>
   /// Clicks with the real mouse. Needed where the handler throws: WinUI hands an
   /// exception raised during a UIA Invoke back to the automation client instead of
   /// raising Application.UnhandledException, which is what a user's click does.
   /// </summary>
   public void MouseClickElement(string automationId)
   {
      AutomationElement element = WaitForElement(automationId);
      MainWindow.SetForeground();
      element.Click();
      Wait.UntilInputIsProcessed();
   }



   /// <summary>Invokes the element carrying <paramref name="automationId"/> (a button, for example).</summary>
   public void ClickElement(string automationId) => Invoke(WaitForElement(automationId));



   /// <summary>
   /// Replaces the text of the element carrying <paramref name="automationId"/>: through the
   /// UI Automation Value pattern (no focus needed), or by typing when the pattern is absent.
   /// </summary>
   public void EnterText(string automationId, string text)
   {
      AutomationElement element = WaitForElement(automationId);
      if(element.Patterns.Value.TryGetPattern(out IValuePattern? value) && !value.IsReadOnly.ValueOrDefault)
      {
         value.SetValue(text);
      }
      else
      {
         element.Focus();
         Keyboard.Type(text);
      }
      Wait.UntilInputIsProcessed();
   }



   /// <summary>Invokes an element through UI Automation, falling back to a mouse click.</summary>
   private static void Invoke(AutomationElement element)
   {
      if(element.Patterns.SelectionItem.TryGetPattern(out ISelectionItemPattern? selection))
      {
         selection.Select();
      }
      else if(element.Patterns.Invoke.TryGetPattern(out IInvokePattern? invoke))
      {
         invoke.Invoke();
      }
      else
      {
         element.Click();
      }
      Wait.UntilInputIsProcessed();
   }



   /// <summary>Waits for the app&apos;s main window, failing fast if the process exits.</summary>
   private Window WaitForMainWindow()
   {
      RetryResult<Window?> result = Retry.WhileNull(
          () =>
          {
             if(App.HasExited)
             {
                throw new InvalidOperationException($"The app exited while starting (exit code 0x{unchecked((uint)App.ExitCode):X8}). Exe: {ExePath}");
             }
             try
             {
                // FlaUI GetMainWindow disposes/reopens its Process on every poll, losing
                // the original exit handle if startup crashes. Retain that handle and
                // discover the window through UIA instead.
                Window? window = Automation.GetDesktop().FindFirstChild(cf => cf.ByProcessId(App.ProcessId)
                   .And(cf.ByControlType(ControlType.Window)))?.AsWindow();
                return window;
             }
             catch
             {
                return null;
             }
          },
          timeout: s_launchTimeout,
          interval: s_pollInterval);
      return result.Result ?? throw new TimeoutException($"The main window did not appear within {s_launchTimeout.TotalSeconds:0}s. Exe: {ExePath}");
   }



   /// <summary>Reads a file the app may still be writing, without locking it.</summary>
   private static string ReadShared(string path)
   {
      if(!File.Exists(path))
      {
         return "";
      }
      using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
      return new StreamReader(stream).ReadToEnd();
   }



   /// <summary>The exe to drive: K22_UITEST_EXE when set, else the repo's newest Debug build.</summary>
   private static string ResolveExePath()
   {
      const string Variable = "K22_UITEST_EXE";
      if(Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } fromEnv)
      {
         return File.Exists(fromEnv) ? fromEnv : throw new FileNotFoundException($"{Variable} points at '{fromEnv}', which does not exist.", fromEnv);
      }

      for(DirectoryInfo? current = new(AppContext.BaseDirectory); current is not null; current = current.Parent)
      {
         if(!File.Exists(Path.Combine(current.FullName, "Qqq.PrintAgent.slnx")))
         {
            continue;
         }

         string bin = Path.Combine(current.FullName, "src", "Qqq.PrintAgent", "bin");
         string? found = Directory.Exists(bin)
            ? Directory.EnumerateFiles(bin, "Qqq.PrintAgent.exe", SearchOption.AllDirectories)
               .Where(path => path.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
               .OrderByDescending(File.GetLastWriteTimeUtc)
               .FirstOrDefault()
            : null;
         return found ?? throw new FileNotFoundException($"Qqq.PrintAgent.exe not found under '{bin}'. Build first: dotnet build Qqq.PrintAgent.slnx -p:Platform=x64");
      }

      throw new FileNotFoundException($"No Qqq.PrintAgent.slnx above '{AppContext.BaseDirectory}'. Set {Variable}.");
   }



   /// <summary>Closes the app (killing it if needed), then disposes the automation and deletes the data folder.</summary>
   public void Dispose()
   {
      try
      {
         App?.Close();
      }
      catch
      {
         // Already gone; the kill below double-checks.
      }
      try
      {
         if(App is { HasExited: false })
         {
            App.Kill();
         }
      }
      catch
      {
         // Best effort.
      }
      App?.Dispose();
      Automation.Dispose();
      try
      {
         Directory.Delete(DataDirectory, recursive: true);
      }
      catch
      {
         // A log handle can linger briefly after exit; the temp cleaner gets it.
      }
   }
}
