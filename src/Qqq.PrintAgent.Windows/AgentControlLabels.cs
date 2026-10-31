namespace Qqq.PrintAgent.Windows;

public static class AgentControlLabels
{
   public static string Support(string value) => value switch
   {
      "SUPPORTED" => "available",
      "LEGACY" => "unavailable on this server",
      _ => "checking support",
   };

   public static string Action(string value) => value switch
   {
      "REFRESH_INVENTORY" => "Refresh printers",
      "CANCEL_SPOOL" => "Cancel one spooler job",
      _ => "Unknown action",
   };

   public static string Outcome(string? value) => value switch
   {
      "INVENTORY_REFRESHED" => "Printers refreshed",
      "CANCEL_REQUESTED" => "Cancellation requested; printed pages cannot be recalled",
      "ALREADY_ABSENT" => "Already absent or completed",
      "OWNERSHIP_MISMATCH" => "Refused: spooler ownership was not verified",
      "UNSUPPORTED" => "Unsupported action",
      "FAILED" => "Failed; inspect status",
      "EXPIRED" => "Expired without execution",
      "OUTCOME_UNKNOWN" => "Outcome unknown; inspect output before retrying",
      _ => "No result recorded",
   };
}
