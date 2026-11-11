/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Tests.Views;

/// <summary>The kinds of thing a view contains or code creates, and the rule that every one an operator acts on has an identifier.</summary>
public sealed partial class XamlContractTests
{
   /// <summary>
   /// The interactive kinds: what an operator acts on with the keyboard or the pointer. They are WinUI's controls that
   /// are pressed or followed, switched, typed in, chosen from, selected in, or expanded, and the items of menus,
   /// navigation, tabs and trees. Each must carry an identifier wherever it appears, in markup or created in code. A
   /// class of the companion that derives from one of these is interactive too.
   /// </summary>
   private static readonly HashSet<string> s_interactive = new(StringComparer.Ordinal)
   {
      "Button", "HyperlinkButton", "Hyperlink", "RepeatButton", "DropDownButton", "SplitButton", "AppBarButton",
      "ToggleButton", "ToggleSplitButton", "AppBarToggleButton", "CheckBox", "RadioButton", "RadioButtons", "ToggleSwitch",
      "TextBox", "PasswordBox", "RichEditBox", "AutoSuggestBox", "NumberBox",
      "ComboBox", "Slider", "RatingControl", "ColorPicker", "CalendarDatePicker", "CalendarView", "DatePicker", "TimePicker",
      "ListView", "GridView", "ListBox", "FlipView", "TreeView", "ItemsView", "NavigationView", "TabView", "Pivot", "SelectorBar", "BreadcrumbBar", "PipsPager",
      "Expander",
      "MenuBarItem", "MenuFlyoutItem", "ToggleMenuFlyoutItem", "RadioMenuFlyoutItem", "MenuFlyoutSubItem", "NavigationViewItem", "TabViewItem", "SelectorBarItem", "TreeViewItem",
   };

   /// <summary>The text kinds: what shows words the window can change while it is shown. These are the candidates for a live region.</summary>
   private static readonly HashSet<string> s_texts = new(StringComparer.Ordinal) { "TextBlock", "RichTextBlock", "InfoBar" };

   /// <summary>
   /// The passive kinds: every other element a view contains or code creates today. Layout (Grid, StackPanel, Border,
   /// ScrollViewer, ContentControl and the row and column definitions); pictures (Image, Ellipse, SymbolIcon,
   /// FontIcon); resources, styles and the window's backdrop; a keyboard accelerator, which is a key and not an
   /// element; the containers whose parts carry the identifiers (Window, UserControl, MenuBar, MenuFlyout, AppKit's
   /// AboutDialog, and ContentDialog, whose three buttons are the platform's own and are named PrimaryButton,
   /// SecondaryButton and CloseButton by its template); a menu's separator; and ComboBoxItem, a choice that is found
   /// through its box by name or position, as a row is found through its list (ADR-0005, "Accessibility"). A class
   /// of the companion is not listed here: it is classified by what it derives from, and one with its own markup is a
   /// view. An element of one of these kinds that takes input (a handler for a press or a key, or a tab stop) needs
   /// an identifier all the same.
   /// </summary>
   private static readonly HashSet<string> s_passive = new(StringComparer.Ordinal)
   {
      "Grid", "StackPanel", "Border", "ScrollViewer", "ContentControl", "RowDefinition", "ColumnDefinition",
      "Image", "Ellipse", "SymbolIcon", "FontIcon",
      "Application", "ResourceDictionary", "XamlControlsResources", "Style", "Setter", "StaticResource", "MicaBackdrop",
      "KeyboardAccelerator",
      "Window", "UserControl", "MenuBar", "MenuFlyout", "ContentDialog", "AboutDialog",
      "MenuFlyoutSeparator", "ComboBoxItem",
   };

   /// <summary>
   /// What the companion's code creates that is no element of a view: measures and other values; brushes and the
   /// sources of pictures; commands; timers, streams and the other things of the operating system; exceptions; the
   /// tray icon; and the services and state of the other assemblies (AppKit's prompter, the pipe's client, the sign-in
   /// flow and the like). Markup can only hold elements, so it needs no such list. Code can create anything, and a
   /// type created in code that is in none of the four lists, and is no class of the companion, fails until someone
   /// says which it is: that is what keeps a control of a kind nobody listed from being created unseen.
   /// </summary>
   private static readonly HashSet<string> s_notElements = new(StringComparer.Ordinal)
   {
      "GridLength", "Thickness", "CornerRadius", "Uri",
      "SolidColorBrush", "BitmapImage", "Icon",
      "RelayCommand", "AsyncRelayCommand",
      "DispatcherTimer", "DispatcherQueueSynchronizationContext", "CancellationTokenSource", "Mutex", "EventWaitHandle", "MemoryStream", "BinaryWriter", "ProcessStartInfo",
      "ArgumentNullException", "InvalidOperationException", "OperationCanceledException",
      "TaskbarIcon",
      "ContentDialogPrompter", "DeviceProfile", "JobDetailsRequestTracker", "JobListState", "MsiUpdateService", "PrinterSort", "QqqBrowserCallback", "QqqSessionClient", "QqqSessionDiagnostics",
      "ServiceClient", "SignInFlow", "TimestampFormat", "UpdateCoordinator",
   };

   /// <summary>
   /// The menus whose items have no identifier, by file and by the name the menu has there, each with the reason. An
   /// item is excepted by what it is added to (<c>menu.Items.Add(...)</c>), not by how many there are; and the menu must
   /// be the one the file gives a tray icon as its <c>ContextFlyout</c>, so an exception cannot outlive what it excuses.
   /// </summary>
   private static readonly (string File, string Menu, string Reason)[] s_nativeMenus =
   [
      ("MainWindow.xaml.cs", "trayMenu", "It is the tray icon's menu. H.NotifyIcon draws that menu as a native Windows popup menu built from these items (its default ContextMenuMode, PopupMenu, and the companion sets no other), so they are never elements on screen and an identifier on them would reach no automation client."),
   ];



   /// <summary>
   /// Every element of an interactive kind in a view has an identifier, written in the markup or set on its
   /// <c>x:Name</c> in the view's code-behind, and so has an element of any other kind that takes input. Every control
   /// of an interactive kind that code creates is named in a <c>SetAutomationId</c>: of the block that creates it for
   /// a local, of the same file for a field or a property; and one block, or one field, holds one such control, so
   /// that a call names one. The only exception is an item added to a menu that Windows draws.
   /// A kind that is in none of the lists fails here as well, in markup and in code, and so does a <c>new(</c> whose
   /// type the reader cannot name, so a control cannot arrive unclassified and therefore unidentified. What code
   /// gives a handler for a press or a key is held as markup is: it is named in a <c>SetAutomationId</c> of the file,
   /// or it is an element of a view that has an identifier.
   /// </summary>
   [Fact]
   public void EveryInteractiveControl_HasAnIdentifier()
   {
      List<string> problems = [.. Unreadable()];
      ControlKinds kinds = Kinds();
      foreach(SourceFile file in _markup)
      {
         var namedInCode = CompanionSources.CodeBehindOf(file, _code).SelectMany(CodeBehind.Named).Select(named => named.Target).ToHashSet(StringComparer.Ordinal);
         problems.AddRange(ViewMarkup.Elements(file).Where(element => !element.Kind.StartsWith("x:", StringComparison.Ordinal)).SelectMany(element => Unidentified(kinds, element, namedInCode)));
      }
      HashSet<(string File, string Menu)> excused = [];
      var identifiedInMarkup = _markup.SelectMany(ViewMarkup.Elements).Where(element => element.Name is not null && !string.IsNullOrWhiteSpace(element.AutomationId)).Select(element => element.Name!).ToHashSet(StringComparer.Ordinal);
      foreach(SourceFile file in _code)
      {
         problems.AddRange(CreatedWithoutAnIdentifier(kinds, file, excused));
         var named = CodeBehind.Named(file).Select(call => call.Target.Split('[')[0]).ToHashSet(StringComparer.Ordinal);
         problems.AddRange(CodeBehind.InputTaken(file).Where(taken => !named.Contains(taken.Target) && !identifiedInMarkup.Contains(taken.Target))
            .Select(taken => $"{file.At(taken.Index)}: {taken.Target} is given a handler for {taken.Event}, so an operator acts on it, and it has no identifier: no SetAutomationId in this file names {taken.Target}, and no element of that name in a view has one. Add AutomationProperties.SetAutomationId({taken.Target}, \"Identifier\") and record it in Views/{RecordedIdentifiers}; or use a control made for the purpose, a Button for something that is pressed."));
      }
      problems.AddRange(NativeMenusThatExcuseNothing(excused));

      Assert.True(problems.Count == 0, Report(problems));
   }



   /// <summary>What a kind is, by the four lists and the companion's own classes.</summary>
   private ControlKinds Kinds() => new(s_interactive, s_texts, s_passive, s_notElements, _markup, _code);



   /// <summary>Holds one element of markup to the rule: an unknown kind is reported, and an interactive element, or one that takes input, without an identifier.</summary>
   private static IEnumerable<string> Unidentified(ControlKinds kinds, ViewMarkup.Element element, HashSet<string> namedInCode)
   {
      ControlKinds.Sort sort = kinds.Of(element.Kind);
      if(sort == ControlKinds.Sort.Unknown)
      {
         yield return $"{element.Where}: no view has had a <{element.Kind}> before, and the contract does not know the kind. Add it to the interactive kinds if an operator acts on it (it then needs an identifier), to the text kinds if it shows words the window changes, and to the passive kinds otherwise. A class of the companion needs no entry: it is classified by what it derives from, once the class exists.";
      }
      bool identified = !string.IsNullOrWhiteSpace(element.AutomationId) || (element.Name is not null && namedInCode.Contains(element.Name));
      if(sort == ControlKinds.Sort.Interactive && !identified)
      {
         yield return $"{element.Where} has no identifier. Give it AutomationProperties.AutomationId, named like the others in Views/{RecordedIdentifiers}, and record it there.";
      }
      else if(sort != ControlKinds.Sort.Interactive && element.TakesInput && !identified)
      {
         yield return $"{element.Where} takes input (a handler for a press or a key, or IsTabStop), so an operator acts on it, and it has no identifier. Give it AutomationProperties.AutomationId and record it in Views/{RecordedIdentifiers}; or use a control made for the purpose, a Button for something that is pressed.";
      }
   }



   /// <summary>Reports, for one file, each <c>new(</c> whose type cannot be named, each created kind the contract does not know, each name that two controls of an interactive kind share in one place, and each such control created without an identifier, apart from the items of the native menus, which are noted in <paramref name="excused"/>.</summary>
   internal static IEnumerable<string> CreatedWithoutAnIdentifier(ControlKinds kinds, SourceFile file, HashSet<(string File, string Menu)> excused)
   {
      IReadOnlyList<(string Target, int Index)> named = CodeBehind.Named(file);
      IReadOnlyList<CodeBehind.Creation> creations = CodeBehind.Creations(file);
      CodeBehind.Creation[] controls = [.. creations.Where(creation => creation.Kind is not null && kinds.Of(creation.Kind) == ControlKinds.Sort.Interactive && !kinds.NamesItself(creation.Kind))];
      foreach(CodeBehind.Creation creation in creations)
      {
         ControlKinds.Sort sort = creation.Kind is null ? ControlKinds.Sort.Unknown : kinds.Of(creation.Kind);
         if(creation.Kind is null)
         {
            yield return $"{creation.Where}: the type this new( creates is not written where the contract can read it (it reads \"Kind name = new(\", an assignment to a field, property or local declared in this file, and \"Kind Name(...) => new(\"). It could be a control. Write the type out: new Kind(...).";
         }
         else if(sort == ControlKinds.Sort.Unknown)
         {
            yield return $"{creation.Where}: code creates a {creation.Kind}, and the contract does not know the kind. Add it to the interactive kinds if an operator acts on it (it then needs an identifier), to the text kinds if it shows words the window changes, to the passive kinds if it is another element of a view, and to s_notElements if it is no element at all (a value, a command, a service).";
         }
         else if(controls.Contains(creation))
         {
            string? problem = SharingAName(file, controls, creation) ?? (Identified(file, named, creation) || Excused(file, creation, excused) ? null : WithoutAnIdentifier(file, named, creation));
            if(problem is not null)
            {
               yield return problem;
            }
         }
      }
   }



   /// <summary>
   /// The stretch of a file in which a control created in code is one control: the whole file for a field or a
   /// property, and for a local the innermost braces around its creation, which is the block that creates it. A
   /// local of the same name in another block (the other branch of an <c>if</c>, another method, an accessor) is
   /// another control, and a <c>SetAutomationId</c> there does not name this one.
   /// </summary>
   private static (int Open, int Close) PlaceOf(SourceFile file, CodeBehind.Creation creation) => creation.Member ? (-1, file.Bare.Length) : CSharpText.BlockAround(file.Bare, creation.Index);



   /// <summary>True when a <c>SetAutomationId</c> names what the creation is assigned to, in the place the creation is one control in.</summary>
   private static bool Identified(SourceFile file, IReadOnlyList<(string Target, int Index)> named, CodeBehind.Creation creation)
   {
      (int open, int close) = PlaceOf(file, creation);
      return creation.Target is not null && named.Any(call => string.Equals(call.Target, creation.Target, StringComparison.Ordinal) && call.Index > open && call.Index < close);
   }



   /// <summary>
   /// Reports a creation that shares the name it is assigned to with another control of an interactive kind in the
   /// place it is one control in; null when it is alone there. A <c>SetAutomationId</c> names a variable, so with two
   /// controls behind one name the contract could not say which of them a call identifies, and the recorded list,
   /// which describes a control by its class and that name, could not tell them apart either. The message is the same
   /// from either of the two, so it is reported once.
   /// </summary>
   private static string? SharingAName(SourceFile file, CodeBehind.Creation[] controls, CodeBehind.Creation creation)
   {
      (int open, int close) = PlaceOf(file, creation);
      CodeBehind.Creation[] same = [.. controls.Where(other => creation.Target is not null && string.Equals(other.Target, creation.Target, StringComparison.Ordinal) && other.Member == creation.Member && other.Index > open && other.Index < close)];
      return same.Length < 2 ? null
         : $"{same[0].Where}: {same.Length} controls are assigned to {creation.Target} in one {(creation.Member ? "file, where it is a field or a property" : "block")} ({string.Join(" and ", same.Select(other => other.Where))}). A SetAutomationId names a variable, so the contract cannot tell which of them a call identifies, and Views/{RecordedIdentifiers} could not tell them apart either. If they are different controls, give each a variable or a field of its own and its own AutomationProperties.SetAutomationId, and record each identifier. If it is one control, create it in one place.";
   }



   /// <summary>True for an item added to a menu that Windows draws, which needs no identifier; the menu is noted in <paramref name="excused"/>.</summary>
   private static bool Excused(SourceFile file, CodeBehind.Creation creation, HashSet<(string File, string Menu)> excused)
   {
      string? menu = CodeBehind.AddedTo(file, creation);
      if(menu is null || !s_nativeMenus.Any(native => string.Equals(native.File, file.Name, StringComparison.Ordinal) && string.Equals(native.Menu, menu, StringComparison.Ordinal)))
      {
         return false;
      }
      excused.Add((file.Name, menu));
      return true;
   }



   /// <summary>Says what to write for a control created without an identifier, by where it is created, and names a <c>SetAutomationId</c> that names the same variable from outside the block, since that is the call a reader would take for this control's.</summary>
   private static string WithoutAnIdentifier(SourceFile file, IReadOnlyList<(string Target, int Index)> named, CodeBehind.Creation creation)
   {
      if(creation.Target is null)
      {
         return $"{creation.Where}: this {creation.Kind} is created inside another expression, so nothing can give it an identifier. Give it a variable, call AutomationProperties.SetAutomationId(variable, \"Identifier\") in the same block, and record the identifier in Views/{RecordedIdentifiers}.";
      }
      if(creation.Member)
      {
         return $"{creation.Where}: this {creation.Kind} is created in code, and no SetAutomationId in this file names {creation.Target}. Add AutomationProperties.SetAutomationId({creation.Target}, \"Identifier\") in this file, named like the others in Views/{RecordedIdentifiers}, and record it there. The contract reads one file at a time: an identifier set in another file of a partial class is not seen, so set it in the file that creates the control.";
      }
      string[] elsewhere = [.. named.Where(call => string.Equals(call.Target, creation.Target, StringComparison.Ordinal)).Select(call => file.At(call.Index))];
      return elsewhere.Length == 0
         ? $"{creation.Where}: this {creation.Kind} is created in code, and no SetAutomationId names {creation.Target}. Add AutomationProperties.SetAutomationId({creation.Target}, \"Identifier\") in the block that creates it (the braces around this line), named like the others in Views/{RecordedIdentifiers}, and record it there."
         : $"{creation.Where}: this {creation.Kind} is created in code, and no SetAutomationId in the block that creates it (the braces around this line) names {creation.Target}. What names {creation.Target} at {string.Join(" and ", elsewhere)} is outside that block, in another branch, another method or an accessor, and so names another control. Add AutomationProperties.SetAutomationId({creation.Target}, \"Identifier\") in the block that creates this one, named like the others in Views/{RecordedIdentifiers}, and record it there. If it is one control that is created in a branch and identified after the branch, create it in one place instead (Kind name = new(...)) and identify it there.";
   }



   /// <summary>Reports each native menu of the table that is no longer the menu of a tray icon, or whose exception no longer excuses anything, and any file that changes how the tray icon draws its menu.</summary>
   private IEnumerable<string> NativeMenusThatExcuseNothing(HashSet<(string File, string Menu)> excused)
   {
      foreach((string name, string menu, string reason) in s_nativeMenus)
      {
         SourceFile? file = _code.FirstOrDefault(code => string.Equals(code.Name, name, StringComparison.Ordinal));
         if(file is null || !CodeBehind.TrayMenus(file).Contains(menu))
         {
            yield return $"{name}: the items of {menu} are excepted from having an identifier, and {menu} is no longer the menu this file gives a tray icon as its ContextFlyout. The exception is: {reason} Remove the exception, and give the items identifiers if they are on screen.";
         }
         else if(!excused.Contains((name, menu)))
         {
            yield return $"{name}: the items of {menu} are excepted from having an identifier, and no item without one is added to it any more. Remove the exception.";
         }
      }
      foreach(SourceFile file in _code.Where(code => code.Bare.Contains("ContextMenuMode", StringComparison.Ordinal)))
      {
         yield return $"{file.Name} sets ContextMenuMode. The exception for the tray icon's menu rests on H.NotifyIcon's default mode, PopupMenu, in which Windows draws the menu; in another mode its items are elements on screen. Give them identifiers and remove the exception, or keep the default mode.";
      }
   }
}
