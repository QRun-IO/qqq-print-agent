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
      Assert.Equal("Loose.cs", CodeBehind.OwnerOf(new SourceFile("Loose.cs", "namespace Sample;"), 0));
      Assert.Equal(
         ["_one", "this, when compact", "this, unless compact", "button in Add(\"From another file\")", "button in Add(\"A label, with a comma\")", "button in Add(\"Other\")"],
         reading.Identifiers.Select(identifier => identifier.Control), StringComparer.Ordinal);
      Assert.Equal([("_one", 5), ("this", 6), ("button", 14)], CodeBehind.Named(file).Select(named => (named.Target, CSharpText.LineOf(file.Text, named.Index))));
   }



   /// <summary>
   /// An identifier that is a variable, is put together, or belongs to a helper nothing calls is not guessed: it is
   /// returned as not written out, with the place a table of generated forms would have to name.
   /// </summary>
   [Theory]
   [InlineData("void Build(string name) { string id = name; SetAutomationId(box, id); }", "Sample.cs | box | id")]
   [InlineData("void Build() { SetAutomationId(box, \"Row\" + index); }", "Sample.cs | box | \"Row\" + index")]
   [InlineData("void Build() { SetAutomationId(this, $\"Row{index}\"); }", "Sample.cs | this | $\"Row{index}\"")]
   [InlineData("void Build() { SetAutomationId(this,\n row.AutomationId); }", "Sample.cs | this | row.AutomationId")]
   [InlineData("void Add(string id) { SetAutomationId(box, id); }", "Sample.cs | box | id")]
   public void Identifier_ThatIsNotWrittenOut_IsReturnedWithItsPlace(string source, string place)
   {
      CodeBehind.Reading reading = CodeBehind.Read([new SourceFile("Sample.cs", source)]);

      Assert.Empty(reading.Identifiers);
      Assert.Empty(reading.Unreadable);
      Assert.Equal(("Sample.cs:1", place), Assert.Single(reading.NotWrittenOut));
   }



   /// <summary>
   /// A call of a forwarding helper that passes no literal is reported at the call, with the place the helper sets
   /// the identifier and both ways out, because the call may be of another method of the same name; and the calls
   /// that do pass a literal are still read, so that the one cause is one message and not also a list of identifiers
   /// that are no longer set.
   /// </summary>
   [Fact]
   public void ForwardingHelper_CalledWithoutALiteral_IsReportedAtTheCall_AndItsOtherCallsAreRead()
   {
      SourceFile rows = new("JobRow.cs", """
         internal sealed class JobRow : Grid
         {
            internal JobRow()
            {
               AddAction(actions, "Print again", "RetryJob");
               AddAction(actions, "Cancel job", "CancelJob");
            }

            private Button AddAction(Panel actions, string label, string id)
            {
               Button button = new() { Content = label };
               AutomationProperties.SetAutomationId(button, id);
               return button;
            }
         }
         """);
      SourceFile other = new("PrinterRow.cs", """
         internal sealed class PrinterRow : Grid
         {
            internal PrinterRow() => AddAction(actions, _settings);

            private static void AddAction(Panel actions, UIElement element) => actions.Children.Add(element);
         }
         """);

      CodeBehind.Reading reading = CodeBehind.Read([rows, other]);
      CodeBehind.Reading passedOn = CodeBehind.Read([new SourceFile("Sample.cs", "void Build(string name) { Add(name); } void Add(string id) { SetAutomationId(box, id); }")]);

      Assert.Equal(["RetryJob", "CancelJob"], reading.Identifiers.Select(identifier => identifier.Value), StringComparer.Ordinal);
      Assert.Empty(reading.NotWrittenOut);
      Assert.Equal(
         "PrinterRow.cs:3: AddAction(actions, _settings) does not pass a string literal as argument 3, and a method called AddAction forwards that argument to SetAutomationId at JobRow.cs:12. "
         + "The contract finds the calls of such a helper by its name alone, in every file of the companion. If this is a call of that helper, write the identifier here as a string literal. "
         + "If it is a call of another method that is also called AddAction, rename one of the two: a helper that forwards an identifier needs a name no other method in the companion has.",
         Assert.Single(reading.Unreadable));
      Assert.Empty(passedOn.Identifiers);
      Assert.Empty(passedOn.NotWrittenOut);
      Assert.StartsWith("Sample.cs:1: Add(name) does not pass a string literal as argument 1, and a method called Add forwards that argument to SetAutomationId at Sample.cs:1.", Assert.Single(passedOn.Unreadable), StringComparison.Ordinal);
   }



   /// <summary>
   /// The owner of a line is the type whose body encloses it, not the first type of the file: a record declared above
   /// a class owns none of the class's lines, and a nested class owns its own. Before any body it is the first type.
   /// </summary>
   [Fact]
   public void TheOwnerOfALine_IsTheTypeThatEnclosesIt()
   {
      SourceFile file = new("JobRowView.cs", """
         internal sealed record JobRowState(bool Expanded);

         internal sealed class JobRowView : Grid
         {
            internal JobRowView() { AutomationProperties.SetAutomationId(_toggle, "ToggleJobDetails"); }

            private sealed class Cell : Button
            {
               internal Cell() { AutomationProperties.SetAutomationId(this, "JobCell"); }
            }

            private void Later() { AutomationProperties.SetAutomationId(_refresh, "RefreshJobDetails"); }
         }
         """);

      Assert.Equal(["JobRowView", "Cell", "JobRowView"], CodeBehind.Read([file]).Identifiers.Select(identifier => identifier.Owner), StringComparer.Ordinal);
      Assert.Equal("JobRowState", CodeBehind.OwnerOf(file, 0));
   }



   /// <summary>
   /// What could set an identifier without the reader seeing it is reported with what to write instead: a call without
   /// its two arguments, <c>AutomationIdProperty</c>, the method handed over as a delegate, a name set through
   /// <c>NameProperty</c>, and markup loaded from a string. A mention inside the call's own arguments is the call's
   /// business.
   /// </summary>
   [Theory]
   [InlineData("void Build() { SetAutomationId(box); }", "Sample.cs:1: SetAutomationId(box) does not take a control and an identifier")]
   [InlineData("void Build() { box.SetValue(AutomationProperties.AutomationIdProperty, \"Box\"); }", "Sample.cs:1: an identifier set or read through AutomationIdProperty cannot be read by the contract. Write AutomationProperties.SetAutomationId(control, \"Identifier\") instead")]
   [InlineData("void Build() { Action<DependencyObject, string> set = AutomationProperties.SetAutomationId; }", "Sample.cs:1: SetAutomationId is mentioned outside a call of SetAutomationId")]
   [InlineData("void Build() { Func<DependencyObject, string> get = AutomationProperties.GetAutomationId; }", "Sample.cs:1: GetAutomationId is mentioned outside a call of SetAutomationId")]
   [InlineData("void Build() { Children.Add((UIElement)XamlReader.Load(\"<Button />\")); }", "Sample.cs:1: XamlReader builds controls from markup in a string")]
   [InlineData("void Build() { row.SetValue(FrameworkElement.NameProperty, name); }", "Sample.cs:1: an element's name set through NameProperty cannot be read by the contract")]
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



   /// <summary>A local that is declared where it is created is a local even when the class has a member of that name, so it is matched within its block only.</summary>
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



   /// <summary>
   /// A declaration reaches to the end of its body, a block or an expression, whatever stands before the body; a local
   /// function lies inside its method, which goes on after it; and what stands between declarations (a property's
   /// accessor, a field's initializer) is in none, so nothing there is read as a parameter of the method before it.
   /// </summary>
   [Fact]
   public void ADeclaration_ReachesToTheEndOfItsBody_AndAnAccessorIsInNone()
   {
      SourceFile file = new("Sample.cs", """
         private Button First<T>(string id) where T : class, new()
         {
            Button button = new() { Content = "}" };
            int Twice(int value) => value * 2;
            AutomationProperties.SetAutomationId(button, id);
            return button;
         }
         internal Button Extra
         {
            get
            {
               Button button = new() { Content = "getter" };
               AutomationProperties.SetAutomationId(button, id);
               return button;
            }
         }
         private readonly Func<Button> _make = () => { Button button = new() { Content = "lambda" }; return button; };
         private static string Text(int count) => $"{count} rows";
         private Sample(string id) : this()
         {
            _id = id;
         }
         private readonly string _after = "after";
         private Button Made() => First<Grid>("FromACall");
         """);
      IReadOnlyList<CSharpText.Method> methods = CSharpText.Methods(file.Code);
      string? In(string text) => CSharpText.Enclosing(methods, file.Code.IndexOf(text, StringComparison.Ordinal))?.Name;

      Assert.Equal(["First", "Twice", "Text", "Sample", "Made"], methods.Select(method => method.Name), StringComparer.Ordinal);
      Assert.Equal(["First", "Twice", "First", null, null, "Text", "Sample", null], new[] { "Button button = new() { Content = \"}\"", "value * 2", "return button;", "\"getter\"", "\"lambda\"", "rows", "_id = id", "\"after\"" }.Select(In), StringComparer.Ordinal);
      Assert.Equal("Text(int count) => $\"{count} rows\";", CSharpText.BodyOf(file.Code, methods[2]));
      Assert.Equal("Twice(int value) => value * 2;", CSharpText.BodyOf(file.Code, methods[1]));
      Assert.Equal("FromACall", Assert.Single(CodeBehind.Read([file]).Identifiers).Value);
      Assert.Equal(("Sample.cs:13", "Sample.cs | button | id"), Assert.Single(CodeBehind.Read([file]).NotWrittenOut));
   }



   /// <summary>
   /// The innermost braces around a position are the block a local is declared in: the two branches of an
   /// <c>if</c> are two blocks, the braces of an initializer are passed over from outside it, a brace in a string or
   /// in a comment is none, and before the first brace it is the whole text.
   /// </summary>
   [Fact]
   public void TheBlockAroundAPosition_IsTheInnermostBraces()
   {
      SourceFile file = new("Sample.cs", """
         void Build()
         {
            string text = "{";
            if(flag)
            {
               Button open = new() { Content = "first" }; // }
            }
            else
            {
               Button open = new() { Content = "second" };
            }
         }
         """);
      (int Open, int Close) Lines(string text)
      {
         (int open, int close) = CSharpText.BlockAround(file.Bare, file.Text.IndexOf(text, StringComparison.Ordinal));
         return (CSharpText.LineOf(file.Text, open), CSharpText.LineOf(file.Text, close));
      }

      Assert.Equal([(5, 7), (9, 11), (2, 12), (6, 6)], new[] { "Button open = new() { Content = \"first\"", "Button open = new() { Content = \"second\"", "string text", "Content = \"first\"" }.Select(Lines));
      Assert.Equal((-1, file.Bare.Length), CSharpText.BlockAround(file.Bare, 0));
   }



   /// <summary>
   /// A name code gives at run time is read with what it names: the class the line is in, a field or a parameter by
   /// its declared type, a local by what it was created as, the object an initializer belongs to. A literal, a
   /// comparison, the declaration of something called Name, an anonymous object and a <c>with</c> expression are not
   /// names given to an element at run time.
   /// </summary>
   [Fact]
   public void NamesGivenAtRunTime_AreReadWithWhatTheyName()
   {
      SourceFile file = new("Sample.cs", """
         internal sealed class Row : Grid
         {
            private readonly Button _late;
            internal Row(Job job, Profile profile)
            {
               Name = $"JobRow{job.JobId}";
               this.Name = job.Title;
               Name = "Fixed";
               _late.Name = job.Title;
               profile.Name = job.Title;
               job.Owner.Name = job.Title;
               var cell = new TextBlock { Text = "x", Name = job.Title };
               cell.Name = job.Title;
               Border border = new() { Name = job.Title };
               var shape = new { Name = job.Title };
               Profile copy = profile with { Name = job.Title };
               Tip tip = new(job) { Header = { Name = job.Title } };
               string Name = job.Title;
               Children.Add(new Button { Name = "Literal", Content = job.Title });
               bool same = Name == job.Title;
               Func<string> later = () => Name = job.Title;
            }
         }
         """);

      IReadOnlyList<CodeBehind.RunTimeName> names = CodeBehind.NamesGivenAtRunTime(file);

      Assert.Equal(
         ["Sample.cs:6 this Row", "Sample.cs:7 this Row", "Sample.cs:9 _late Button", "Sample.cs:10 profile Profile", "Sample.cs:11 an expression ", "Sample.cs:12 a new TextBlock TextBlock", "Sample.cs:13 cell TextBlock",
            "Sample.cs:14 a new object Border", "Sample.cs:17 the member an initializer fills ", "Sample.cs:21 this Row"],
         names.Select(name => $"{name.Where} {name.On} {name.Kind}"), StringComparer.Ordinal);
      Assert.Equal("$\"JobRow{job.JobId}\"", names[0].Value);
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
