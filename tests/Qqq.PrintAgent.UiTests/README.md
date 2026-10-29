# Print Agent UI acceptance

Run from an **administrator terminal on an interactive Windows desktop**. The app manifest requires administrator access for the service administration pipe; a lower-integrity UI automation runner cannot inspect it. The fixture fails with an explicit explanation when these prerequisites are missing. It never handles UAC prompts.

Build the companion first, then run:

```powershell
dotnet test tests/Qqq.PrintAgent.UiTests/Qqq.PrintAgent.UiTests.csproj -p:Platform=x64
```

Set `K22_UITEST_EXE` to test a specific published companion executable. Each session isolates presentation settings in a temporary directory. Activity logs intentionally use the same protected machine directory as the service; test launches add companion startup entries there. Tests query the real local service for status/printer inventory if it is running, but never enroll a device, change service credentials, or submit a print job.

Coverage: native Printers/Jobs/Settings navigation, connection controls, local rejection of invalid enrollment input, splash dismissal, About version, one shared AppKit log window from navigation/footer, and saved theme. Initial status requests can take 30 seconds when the service is absent.

2026-10-29 validation: test project compiled with zero warnings/errors. The actual companion launched successfully and displayed actionable service-unavailable guidance after its timeout. The complete automated desktop suite has not been executed; it requires the elevated interactive runner above.
