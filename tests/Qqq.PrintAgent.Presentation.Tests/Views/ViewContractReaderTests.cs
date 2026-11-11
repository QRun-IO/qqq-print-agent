/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Tests.Views;

/// <summary>
/// The readers the view contract stands on, tried on small pieces of source written here. The contract itself runs
/// against the real companion, where a reader that stopped seeing something would only make a test pass; these hold
/// each form the readers claim to see, and that what they cannot read is reported. The readers of markup, of the
/// suites and of the kinds are tried in ViewContractMarkupAndSuiteReaderTests.
/// </summary>
public sealed class ViewContractReaderTests
{
   /// <summary>A sentence in a comment is not source, a string that contains two slashes does not start a comment, and a sentence in a string is not code.</summary>
   [Fact]
   public void Comments_AreNotRead_AndAnAddressInAStringDoesNotStartOne()
   {
      SourceFile file = new("Sample.cs", """
         // AutomationProperties.SetAutomationId(first, "InALineComment");
         /* SetAutomationId(second, "InABlockComment"); */
         string address = "https://example.invalid/"; char quote = '"'; AutomationProperties.SetAutomationId(third, "AfterAnAddress");
         string words = "start a new Button here"; bool tagged = string.Equals(AutomationProperties.GetAutomationId(third), "AfterAnAddress", StringComparison.Ordinal);
         """);

      CodeBehind.Reading reading = CodeBehind.Read([file]);

      Assert.Equal(["AfterAnAddress"], reading.Identifiers.Select(identifier => identifier.Value), StringComparer.Ordinal);
      Assert.Empty(reading.Unreadable);
      Assert.Empty(reading.NotWrittenOut);
      Assert.Equal(file.Text.Length, file.Code.Length);
      Assert.Equal(file.Text.Length, file.Bare.Length);
      Assert.Equal("Sample.cs:3", file.At(file.Code.IndexOf("third", StringComparison.Ordinal)));
      Assert.Contains("\"https://example.invalid/\"", file.Code, StringComparison.Ordinal);
      Assert.DoesNotContain("example", file.Bare, StringComparison.Ordinal);
      Assert.Empty(CodeBehind.Creations(file));
   }



   /// <summary>An identifier is read from a literal, from a choice between two literals, and from a parameter whose callers each write a literal, with the control it is set on described by its class and target, without a file name or a line number.</summary>
   [Fact]
   public void Identifier_IsReadFromALiteralAChoiceAndAForwardedParameter()
   {
      SourceFile file = new("Sample.cs", """
         internal sealed class Sample : StackPanel
         {
            internal Sample(bool compact) : base()
            {
               AutomationProperties.SetAutomationId(this._one, "One");
               Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(this, compact ? "Small" : "Large");
               _first = Add(actions, "A label, with a comma", "Forwarded", _ => Run(this));
               _second = Add(actions, "Other", "ForwardedToo", Run);
            }

            internal Button Add(Panel actions, string label, string id, Func<Sample, Task> action)
            {
               Button button = new() { Content = label };
               AutomationProperties.SetAutomationId(button, id);
               return button;
            }
         }
         """);
      SourceFile other = new("Other.cs", """
         internal sealed class Other
         {
            void Build(Sample sample) => sample.Add(toolbar, "From another file", "FromAnotherFile", Run);
         }
         """);

      CodeBehind.Reading reading = CodeBehind.Read([other, file]);

      Assert.Empty(reading.Unreadable);
      Assert.Empty(reading.NotWrittenOut);
      Assert.Equal(["One", "Small", "Large", "FromAnotherFile", "Forwarded", "ForwardedToo"], reading.Identifiers.Select(identifier => identifier.Value), StringComparer.Ordinal);
      Assert.Equal(["_one", "this", "this", "button", "button", "button"], reading.Identifiers.Select(identifier => identifier.Target), StringComparer.Ordinal);
      Assert.Equal(["Sample.cs:5", "Sample.cs:6", "Sample.cs:6", "Other.cs:3", "Sample.cs:7", "Sample.cs:8"], reading.Identifiers.Select(identifier => identifier.Where), StringComparer.Ordinal);
      Assert.Equal(["Sample", "Sample", "Sample", "Other", "Sample", "Sample"], reading.Identifiers.Select(identifier => identifier.Owner), StringComparer.Ordinal);
      Assert.Equal("Loose.cs", CodeBehind.OwnerOf(new SourceFile("Loose.cs", "namespace Sample;")));
      Assert.Equal(
         ["_one", "this, when compact", "this, unless compact", "button in Add(\"From another file\")", "button in Add(\"A label, with a comma\")", "button in Add(\"Other\")"],
         reading.Identifiers.Select(identifier => identifier.Control), StringComparer.Ordinal);
      Assert.Equal([("_one", 5), ("this", 6), ("button", 14)], CodeBehind.Named(file).Select(named => (named.Target, CSharpText.LineOf(file.Text, named.Index))));
   }



   /// <summary>
   /// An identifier that is a variable, is put together, comes from a caller that passes something else than a literal,
   /// or belongs to a helper nothing calls is not guessed: it is returned as not written out, with the place a table
   /// of generated forms would have to name.
   /// </summary>
   [Theory]
   [InlineData("void Build(string name) { string id = name; SetAutomationId(box, id); }", "Sample.cs | box | id")]
   [InlineData("void Build() { SetAutomationId(box, \"Row\" + index); }", "Sample.cs | box | \"Row\" + index")]
   [InlineData("void Build() { SetAutomationId(this, $\"Row{index}\"); }", "Sample.cs | this | $\"Row{index}\"")]
   [InlineData("void Build() { SetAutomationId(this,\n row.AutomationId); }", "Sample.cs | this | row.AutomationId")]
   [InlineData("void Build(string name) { Add(name); } void Add(string id) { SetAutomationId(box, id); }", "Sample.cs | box | id")]
   [InlineData("void Add(string id) { SetAutomationId(box, id); }", "Sample.cs | box | id")]
   public void Identifier_ThatIsNotWrittenOut_IsReturnedWithItsPlace(string source, string place)
   {
      CodeBehind.Reading reading = CodeBehind.Read([new SourceFile("Sample.cs", source)]);

      Assert.Empty(reading.Identifiers);
      Assert.Empty(reading.Unreadable);
      Assert.Equal(("Sample.cs:1", place), Assert.Single(reading.NotWrittenOut));
   }



   /// <summary>
   /// What could set an identifier without the reader seeing it is reported with what to write instead: a call without
   /// its two arguments, <c>AutomationIdProperty</c>, the method handed over as a delegate, and markup loaded from a
   /// string. A mention inside the call's own arguments is the call's business.
   /// </summary>
   [Theory]
   [InlineData("void Build() { SetAutomationId(box); }", "Sample.cs:1: SetAutomationId(box) does not take a control and an identifier")]
   [InlineData("void Build() { box.SetValue(AutomationProperties.AutomationIdProperty, \"Box\"); }", "Sample.cs:1: an identifier set or read through AutomationIdProperty cannot be read by the contract. Write AutomationProperties.SetAutomationId(control, \"Identifier\") instead")]
   [InlineData("void Build() { Action<DependencyObject, string> set = AutomationProperties.SetAutomationId; }", "Sample.cs:1: SetAutomationId is mentioned outside a call of SetAutomationId")]
   [InlineData("void Build() { Func<DependencyObject, string> get = AutomationProperties.GetAutomationId; }", "Sample.cs:1: GetAutomationId is mentioned outside a call of SetAutomationId")]
   [InlineData("void Build() { Children.Add((UIElement)XamlReader.Load(\"<Button />\")); }", "Sample.cs:1: XamlReader builds controls from markup in a string")]
   public void WhatCouldSetAnIdentifierUnseen_IsReported(string source, string message)
   {
      CodeBehind.Reading reading = CodeBehind.Read([new SourceFile("Sample.cs", source)]);

      Assert.Empty(reading.Identifiers);
      Assert.Empty(reading.NotWrittenOut);
      Assert.StartsWith(message, Assert.Single(reading.Unreadable), StringComparison.Ordinal);
   }



   /// <summary>
   /// A control created in code is seen as a field, a property, a local of either spelling, nullable, with a namespace
   /// or <c>global::</c> in front, as a field assigned later, inside another expression (which leaves it no name) and
   /// as the whole body of a method; an array, a generic list, a built-in type, an anonymous object and a generic
   /// constraint are not controls. A field or a property is a member, a local is not.
   /// </summary>
   [Fact]
   public void Creation_IsSeenInEachForm_WithWhatItIsAssignedTo()
   {
      SourceFile file = new("Sample.cs", """
         private readonly Button _field = new() { Width = 32 };
         internal CheckBox Property { get; } = new() { IsChecked = true };
         private readonly Button _late;
         private readonly Dictionary<string, Button> _rows = new(StringComparer.Ordinal);
         private void Build<T>() where T : Panel, new()
         {
            var local = new HyperlinkButton { Content = "local" };
            ToggleSwitch typed = new() { Header = "typed" };
            Button? maybe = new();
            Children.Add(new Button { Content = "inside" });
            Button[] many = new Button[2];
            List<Button> list = new List<Button>();
            _late = new() { Content = "late" };
            this._late = new Microsoft.UI.Xaml.Controls.Button();
            Children.Add(new global::Microsoft.UI.Xaml.Controls.Button { Content = "global" });
            other.Content = new Button();
            string text = new('x', 3);
            var shape = new { Width = 1 };
            int[] numbers = new[] { 1, 2 };
            TextBlock words = new() { Text = "not interactive" };
         }
         private static Button Make() => new() { Content = "made" };
         """);

      IReadOnlyList<CodeBehind.Creation> creations = CodeBehind.Creations(file);

      Assert.Equal(
         ["Button _field member", "CheckBox Property member", "HyperlinkButton local", "ToggleSwitch typed", "Button maybe", "Button ", "Button _late member", "Button _late member", "Button ", "Button ", "TextBlock words", "Button "],
         creations.Select(creation => $"{creation.Kind} {creation.Target}{(creation.Member ? " member" : string.Empty)}"), StringComparer.Ordinal);
      Assert.Equal("Sample.cs:10", creations[5].Where);
      Assert.Equal("Sample.cs:22", creations[11].Where);
   }



   /// <summary>A local that is declared where it is created is a local even when the class has a member of that name, so it is matched within its method only.</summary>
   [Fact]
   public void Creation_OfALocalNamedLikeAMember_IsALocal()
   {
      SourceFile file = new("Sample.cs", """
         private readonly Button button = new();
         private void Build()
         {
            Button button = new() { Content = "local" };
            var other = new Button();
            button = new Button();
         }
         """);

      Assert.Equal([true, false, false, true], CodeBehind.Creations(file).Select(creation => creation.Member));
   }



   /// <summary>A <c>new(</c> whose type is written somewhere the reader does not look is returned without a kind, never left out: a return in a block, an argument, an item of a collection, an assignment to something no declaration in the file names, and an assignment to a name the file declares with two types.</summary>
   [Theory]
   [InlineData("private void First(TextBlock copy) { } private void Second() { Button copy; copy = new(); }")]
   [InlineData("private Button Make() { return new() { Content = \"made\" }; }")]
   [InlineData("private void Build() { Children.Add(new()); }")]
   [InlineData("private void Build() { Button[] many = [new(), new Button()]; }")]
   [InlineData("private void Build() { _elsewhere = new(); }")]
   public void Creation_WhoseTypeCannotBeNamed_IsReturnedWithoutAKind(string source)
   {
      IReadOnlyList<CodeBehind.Creation> creations = CodeBehind.Creations(new SourceFile("Sample.cs", source));

      Assert.Contains(creations, creation => creation.Kind is null && string.Equals(creation.Where, "Sample.cs:1", StringComparison.Ordinal));
   }



   /// <summary>A handler for a press or a key is read with what it is put on: a name, the last name of a path, an element of a collection, or the class itself; other events are not input.</summary>
   [Fact]
   public void InputHandlers_AreReadWithWhatTheyArePutOn()
   {
      SourceFile file = new("Sample.cs", """
         private void Build()
         {
            clear.Click += async (_, _) => await ClearAsync();
            row.Header.Tapped += Open;
            _cells[0].PointerPressed += Open;
            this.KeyDown += Typed;
            DoubleTapped += Open;
            _timer.Tick += Refresh;
            Loaded += Ready;
            bool same = Clicks == 2;
         }
         """);

      Assert.Equal([("clear", "Click", 3), ("Header", "Tapped", 4), ("_cells", "PointerPressed", 5), ("this", "KeyDown", 6), ("this", "DoubleTapped", 7)], CodeBehind.InputTaken(file).Select(taken => (taken.Target, taken.Event, CSharpText.LineOf(file.Text, taken.Index))));
   }



   /// <summary>Two positions are in the same method when no declaration starts between them, so a local of one method is not matched to a call that names a local of the same name in another.</summary>
   [Fact]
   public void TheSameMethod_IsUpToTheNextDeclaration()
   {
      SourceFile file = new("Sample.cs", """
         private Button First()
         {
            Button button = new();
            AutomationProperties.SetAutomationId(button, "First");
            return button;
         }
         private Button Second()
         {
            Button button = new();
            return button;
         }
         """);
      IReadOnlyList<CSharpText.Method> methods = CSharpText.Methods(file.Code);
      IReadOnlyList<CodeBehind.Creation> creations = CodeBehind.Creations(file);
      int named = Assert.Single(CodeBehind.Named(file)).Index;

      Assert.Equal(["First", "Second"], methods.Select(method => method.Name), StringComparer.Ordinal);
      Assert.True(CSharpText.InTheSameMethod(methods, creations[0].Index, named));
      Assert.False(CSharpText.InTheSameMethod(methods, creations[1].Index, named));
      Assert.True(CSharpText.InTheSameMethod(methods, 0, 1));
   }



   /// <summary>A class, a record and a struct are read with the type they derive from, with or without a namespace, <c>global::</c> or a primary constructor, and a partial declaration without a base is read without one.</summary>
   [Fact]
   public void Types_AreReadWithWhatTheyDeriveFrom()
   {
      SourceFile file = new("Sample.cs", """
         internal sealed class RowView : Grid { }
         public sealed partial class Window { }
         internal sealed class Prompter(IPrompter inner) : IPrompter { }
         internal sealed class HealthButton(string glyph) : global::Microsoft.UI.Xaml.Controls.Button { }
         internal sealed record Row(string Name) : Item(Name);
         internal readonly record struct Measure(int Width);
         """);

      Assert.Equal([("RowView", "Grid"), ("Window", null), ("Prompter", "IPrompter"), ("HealthButton", "Button"), ("Row", "Item"), ("Measure", null)], CodeBehind.Types(file));
   }



   /// <summary>
   /// The texts the files write are read by name: assigned directly, assigned through another object from another
   /// file, and passed to a method that assigns its parameter. A comparison, and another property that ends in the
   /// same word, are not writes. Live regions set in code and the announcement raised for an element are read, and a
   /// raise in another form is reported.
   /// </summary>
   [Fact]
   public void TextsLiveRegionsAndAnnouncements_AreRead()
   {
      SourceFile file = new("Sample.cs", """
         internal sealed class Sample
         {
            void Show()
            {
               Status.Text = "ready";
               Bar.Message = message;
               Bar.Title = title;
               if(Other.Text == "ready") { Box.PlaceholderText = "hint"; }
               Write(Result, "saved");
               Write(pane.Outcome, "saved");
               Count(Untouched, 3);
               AutomationProperties.SetLiveSetting(Status, AutomationLiveSetting.Polite);
               FrameworkElementAutomationPeer.FromElement(Status)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
               peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }

            private static void Write(TextBlock target, string text) => target.Text = text;

            private static void Count(TextBlock target, int count) { Total = count; }
         }
         """);
      SourceFile other = new("Other.cs", "void Tell(Sample sample) { sample.Pane.Remote.Text = \"told\"; }");
      List<string> unreadable = [];

      Assert.Equal(["Bar", "Outcome", "Remote", "Result", "Status", "target"], CodeBehind.TextsWritten([file, other]).Order(StringComparer.Ordinal), StringComparer.Ordinal);
      Assert.Equal(("Status", "Polite"), Assert.Single(CodeBehind.LiveSettings(file).Select(setting => (setting.Key, setting.Value))));
      Assert.Equal(["Status"], CodeBehind.Announced(file, unreadable), StringComparer.Ordinal);
      Assert.StartsWith("Sample.cs:14: LiveRegionChanged is used here in a form the contract cannot read.", Assert.Single(unreadable), StringComparison.Ordinal);
   }



   /// <summary>An item is read as added to a menu when it is created in the menu's <c>Items.Add</c> or assigned to a name that is added there, and the menu of a tray icon is the one its initializer gives it as its ContextFlyout.</summary>
   [Fact]
   public void MenuItems_AreReadWithTheMenuTheyAreAddedTo()
   {
      SourceFile file = new("Sample.cs", """
         private readonly MenuFlyoutItem _status;
         private void Build()
         {
            _status = new MenuFlyoutItem { Text = "Status" };
            MenuFlyout trayMenu = new();
            trayMenu.Items.Add(_status);
            trayMenu.Items.Add(new MenuFlyoutItem { Text = "Open" });
            MenuFlyout pageMenu = new();
            pageMenu.Items.Add(new MenuFlyoutItem { Text = "Sign out" });
            Children.Add(new MenuFlyoutItem { Text = "Loose" });
            _tray = new TaskbarIcon { ToolTipText = Tip("a, b"), ContextFlyout = trayMenu, LeftClickCommand = new RelayCommand(Show) };
            Navigation.ContextFlyout = pageMenu;
         }
         """);

      IEnumerable<string?> menus = CodeBehind.Creations(file).Where(creation => string.Equals(creation.Kind, "MenuFlyoutItem", StringComparison.Ordinal)).Select(creation => CodeBehind.AddedTo(file, creation));

      Assert.Equal(["trayMenu", "trayMenu", "pageMenu", null], menus, StringComparer.Ordinal);
      Assert.Equal(["trayMenu"], CodeBehind.TrayMenus(file), StringComparer.Ordinal);
   }
}
