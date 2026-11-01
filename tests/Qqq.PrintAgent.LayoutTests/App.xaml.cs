using KofTwentyTwo.AppKit.Logging;
using Microsoft.UI.Xaml;

namespace Qqq.PrintAgent;

public partial class App : Application
{
   private Window? _window;
   internal static IActivityLog Log { get; } = AppIdentity.CreateLog();

   public App()
   {
      InitializeComponent();
      DebugSettings.LayoutCycleTracingLevel = LayoutCycleTracingLevel.High;
      UnhandledException += (_, args) =>
      {
         // A real native layout failure is a failing result, never a swallowed test success.
         File.WriteAllText(Path.Combine(Program.EvidenceDirectory, "unhandled.txt"), args.Exception.ToString());
         Environment.ExitCode = 1;
         args.Handled = true;
         (_window as IDisposable)?.Dispose();
         Exit();
      };
   }

   protected override async void OnLaunched(LaunchActivatedEventArgs args)
   {
      var service = new SyntheticService();
      try
      {
         var mainWindow = new MainWindow(service.SendAsync);
         _window = mainWindow;
         mainWindow.AppWindow.Resize(new global::Windows.Graphics.SizeInt32(Program.WindowWidth, Program.WindowHeight));
         _window.Activate();
         await LayoutScenario.RunAsync(mainWindow, service);
         Environment.ExitCode = 0;
      }
      catch(Exception exception)
      {
         File.WriteAllText(Path.Combine(Program.EvidenceDirectory, "failure.txt"), exception.ToString());
         Environment.ExitCode = 1;
      }
      finally
      {
         (_window as IDisposable)?.Dispose();
         Exit();
      }
   }
}
