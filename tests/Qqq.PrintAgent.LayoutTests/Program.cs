using Microsoft.UI.Dispatching;

namespace Qqq.PrintAgent;

internal static class Program
{
   internal static EventWaitHandle? ActivationSignal => null;
   internal static string EvidenceDirectory { get; private set; } = string.Empty;
   internal static int WindowWidth { get; private set; } = 1100;
   internal static int WindowHeight { get; private set; } = 760;
   internal static bool RefreshDuringDialog { get; private set; }

   [STAThread]
   private static int Main(string[] args)
   {
      if(args.Length is not (1 or 3 or 4) || !Path.IsPathFullyQualified(args[0]) || Directory.Exists(args[0]))
      {
         return 2;
      }
      if(args.Length >= 3)
      {
         if(!int.TryParse(args[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int width)
            || !int.TryParse(args[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int height)
            || width is < 800 or > 1400 || height is < 560 or > 1000)
         {
            return 2;
         }
         WindowWidth = width;
         WindowHeight = height;
      }
      if(args.Length == 4)
      {
         if(!string.Equals(args[3], "--refresh-during-dialog", StringComparison.Ordinal))
         {
            return 2;
         }
         RefreshDuringDialog = string.Equals(args[3], "--refresh-during-dialog", StringComparison.Ordinal);
      }
      EvidenceDirectory = Path.GetFullPath(args[0]);
      Directory.CreateDirectory(EvidenceDirectory);
      string profile = Path.Combine(EvidenceDirectory, "profile");
      Directory.CreateDirectory(profile);
      Environment.SetEnvironmentVariable("QQQ_PRINT_AGENT_DATA_DIR", profile);
      File.WriteAllText(Path.Combine(profile, "settings.json"), "{\"ShowSplashScreen\":false,\"CheckForUpdatesOnStartup\":false}");
      WinRT.ComWrappersSupport.InitializeComWrappers();
      Microsoft.UI.Xaml.Application.Start(callbackParameters =>
      {
         SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
         _ = new App();
      });
      return Environment.ExitCode;
   }
}
