/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Tests.Views;

/// <summary>
/// The readers the view contract stands on, tried on small pieces of source written here. The contract itself runs
/// against the real companion, where a reader that stopped seeing something would only make a test pass; these hold
/// each form the readers claim to see, and that what they cannot read is reported.
/// </summary>
public sealed class ViewContractReaderTests
{
   /// <summary>A sentence in a comment is not source, and a string that contains two slashes does not start a comment.</summary>
   [Fact]
   public void Comments_AreNotRead_AndAnAddressInAStringDoesNotStartOne()
   {
      SourceFile file = new("Sample.cs", """
         // AutomationProperties.SetAutomationId(first, "InALineComment");
         /* SetAutomationId(second, "InABlockComment"); */
         string address = "https://example.invalid/"; char quote = '"'; AutomationProperties.SetAutomationId(third, "AfterAnAddress");
         """);
      List<string> unreadable = [];

      Assert.Equal(["AfterAnAddress"], CodeBehind.Identifiers(file, unreadable).Select(identifier => identifier.Value), StringComparer.Ordinal);
      Assert.Empty(unreadable);
      Assert.Equal(file.Text.Length, file.Code.Length);
      Assert.Equal("Sample.cs:3", file.At(file.Code.IndexOf("third", StringComparison.Ordinal)));
   }



   /// <summary>An identifier is read from a literal, from a choice between two literals, and from a parameter whose callers each write a literal, with the control it is set on.</summary>
   [Fact]
   public void Identifier_IsReadFromALiteralAChoiceAndAForwardedParameter()
   {
      SourceFile file = new("Sample.cs", """
         internal sealed class Sample : StackPanel
         {
            internal Sample(bool compact) : base()
            {
               AutomationProperties.SetAutomationId(_one, "One");
               Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(this, compact ? "Small" : "Large");
               _first = Add(actions, "A label, with a comma", "Forwarded", _ => Run(this));
               _second = Add(actions, "Other", "ForwardedToo", Run);
            }

            private Button Add(Panel actions, string label, string id, Func<Sample, Task> action)
            {
               Button button = new() { Content = label };
               AutomationProperties.SetAutomationId(button, id);
               return button;
            }
         }
         """);
      List<string> unreadable = [];

      IReadOnlyList<CodeBehind.Identifier> identifiers = CodeBehind.Identifiers(file, unreadable);

      Assert.Empty(unreadable);
      Assert.Equal(["One", "Small", "Large", "Forwarded", "ForwardedToo"], identifiers.Select(identifier => identifier.Value), StringComparer.Ordinal);
      Assert.Equal(["_one", "this", "this", "button", "button"], identifiers.Select(identifier => identifier.Target), StringComparer.Ordinal);
      Assert.Equal(["Sample.cs:5", "Sample.cs:6", "Sample.cs:6", "Sample.cs:7", "Sample.cs:8"], identifiers.Select(identifier => identifier.Where), StringComparer.Ordinal);
   }



   /// <summary>An identifier that is a variable, is put together, comes from a caller that passes something else than a literal, or belongs to a helper nothing in the file calls is reported with its place, and is not guessed.</summary>
   [Theory]
   [InlineData("void Build(string name) { string id = name; SetAutomationId(box, id); }")]
   [InlineData("void Build() { SetAutomationId(box, \"Row\" + index); }")]
   [InlineData("void Build() { SetAutomationId(box, $\"Row{index}\"); }")]
   [InlineData("void Build(string name) { Add(name); } void Add(string id) { SetAutomationId(box, id); }")]
   [InlineData("void Add(string id) { SetAutomationId(box, id); }")]
   [InlineData("void Build() { SetAutomationId(box); }")]
   public void Identifier_ThatCannotBeRead_IsReported(string source)
   {
      List<string> unreadable = [];

      IReadOnlyList<CodeBehind.Identifier> identifiers = CodeBehind.Identifiers(new SourceFile("Sample.cs", source), unreadable);

      Assert.Empty(identifiers);
      Assert.StartsWith("Sample.cs:1: the identifier in SetAutomationId(", Assert.Single(unreadable), StringComparison.Ordinal);
   }



   /// <summary>A control created in code is seen as a field, a property, a local of either spelling, a qualified type, inside another expression (which leaves it no name) and as the whole body of a method; an array and a generic list of the kind are not controls.</summary>
   [Fact]
   public void Creation_IsSeenInEachForm_WithWhatItIsAssignedTo()
   {
      SourceFile file = new("Sample.cs", """
         private readonly Button _field = new() { Width = 32 };
         internal CheckBox Property { get; } = new() { IsChecked = true };
         private void Build()
         {
            var local = new HyperlinkButton { Content = "local" };
            ToggleSwitch typed = new() { Header = "typed" };
            Children.Add(new Button { Content = "inside" });
            Button[] many = new Button[2];
            List<Button> list = new List<Button>();
            _late = new Microsoft.UI.Xaml.Controls.Button();
            TextBlock text = new() { Text = "not interactive" };
         }
         private static Button Make() => new() { Content = "made" };
         """);
      HashSet<string> kinds = new(StringComparer.Ordinal) { "Button", "CheckBox", "HyperlinkButton", "ToggleSwitch" };

      IReadOnlyList<CodeBehind.Creation> creations = CodeBehind.Creations(file, kinds);

      Assert.Equal(["Button _field", "CheckBox Property", "HyperlinkButton local", "ToggleSwitch typed", "Button ", "Button _late", "Button "], creations.Select(creation => creation.Kind + " " + creation.Target), StringComparer.Ordinal);
      Assert.Equal("Sample.cs:7", creations[4].Where);
      Assert.Equal("Sample.cs:13", creations[6].Where);
   }



   /// <summary>A class is read with the type it derives from, and the texts and live regions a file writes by name are read, while a comparison and another property that ends in the same word are not writes.</summary>
   [Fact]
   public void ClassesTextsAndLiveRegions_AreRead()
   {
      SourceFile file = new("Sample.cs", """
         internal sealed class RowView : Grid { }
         public sealed partial class Window { }
         internal sealed class Prompter(IPrompter inner) : IPrompter
         {
            void Show()
            {
               Status.Text = "ready";
               Bar.Message = message;
               Bar.Title = title;
               if(Other.Text == "ready") { Box.PlaceholderText = "hint"; }
               AutomationProperties.SetLiveSetting(Status, AutomationLiveSetting.Polite);
            }
         }
         """);

      Assert.Equal([("RowView", "Grid"), ("Prompter", "IPrompter")], CodeBehind.Subclasses(file));
      Assert.Equal(["Bar", "Status"], CodeBehind.TextsWritten(file).Order(StringComparer.Ordinal), StringComparer.Ordinal);
      Assert.Equal("Polite", Assert.Single(CodeBehind.LiveSettings(file)).Value);
   }



   /// <summary>Markup is read element by element: a property element is not one, the language's own elements are marked, and a text that follows a one-way binding is seen to change while one that is bound once is not.</summary>
   [Fact]
   public void Markup_IsReadWithNamesIdentifiersLiveRegionsAndBindings()
   {
      SourceFile file = new("Views/Sample.xaml", """
         <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Page.Resources><x:Double x:Key="Width">760</x:Double></Page.Resources>
            <Button x:Name="Save" Content="Save changes" AutomationProperties.AutomationId="SaveChanges" />
            <TextBlock x:Name="Result" Text="{x:Bind Model.Result, Mode=OneWay}" AutomationProperties.LiveSetting="Polite" />
            <TextBlock Text="{x:Bind Model.Title}" />
            <InfoBar Message="{Binding Message}" />
         </Page>
         """);

      IReadOnlyList<ViewMarkup.Element> elements = ViewMarkup.Elements(file);

      Assert.Equal(["Page", "x:Double", "Button", "TextBlock", "TextBlock", "InfoBar"], elements.Select(element => element.Kind), StringComparer.Ordinal);
      Assert.Equal(new ViewMarkup.Element("Views/Sample.xaml:3 <Button> \"Save changes\"", "Button", "Save", "SaveChanges", null, Bound: false), elements[2]);
      Assert.Equal(new ViewMarkup.Element("Views/Sample.xaml:4 <TextBlock> \"{x:Bind Model.Result, Mode=OneWay}\"", "TextBlock", "Result", null, "Polite", Bound: true), elements[3]);
      Assert.Equal([false, false, false, true, false, true], elements.Select(element => element.Bound));
   }



   /// <summary>A view's code-behind is the files in its folder that start with its name and a dot, and no other view's.</summary>
   [Fact]
   public void CodeBehind_IsTheFilesThatStartWithTheViewsName()
   {
      SourceFile[] code = [new("MainWindow.xaml.cs", ""), new("MainWindow.Jobs.cs", ""), new("MainWindowHelper.cs", ""), new("Views/MainWindow.xaml.cs", ""), new("JobRowView.cs", "")];

      Assert.Equal(["MainWindow.xaml.cs", "MainWindow.Jobs.cs"], CompanionSources.CodeBehindOf(new SourceFile("MainWindow.xaml", ""), code).Select(file => file.Name), StringComparer.Ordinal);
      Assert.Equal(["Views/MainWindow.xaml.cs"], CompanionSources.CodeBehindOf(new SourceFile("Views/MainWindow.xaml", ""), code).Select(file => file.Name), StringComparer.Ordinal);
   }



   /// <summary>The suites' lookups are read from a literal, from each position a lookup takes its identifier at, from a loop over an array of literals, from inside a lambda and from a comparison; a helper that forwards its parameter to a lookup adds nothing itself.</summary>
   [Fact]
   public void Lookups_AreReadFromLiteralsLoopsAndComparisons()
   {
      SourceFile file = new("UiTests/Sample.cs", """
         public void Test()
         {
            session.WaitForElement("Direct");
            session.InvokeMenuItem("File", "InAMenu");
            string[] headers = ["First", "Second"];
            foreach(string header in headers)
            {
               AutomationElement button = session.WaitForElement(header);
            }
            row.FindFirstDescendant(cf => cf.ByAutomationId("InALambda"));
            session.ClickButton("Close");
            Button print = buttons.Single(button => string.Equals(AutomationProperties.GetAutomationId(button), "Compared", StringComparison.Ordinal));
            var list = (ListView)root.FindName("PrintersList");
            ContentDialog dialog = Field<ContentDialog>(window, "ConnectionDialog");
         }

         public void ClickElement(string automationId) => Invoke(WaitForElement(automationId));

         public AutomationElement WaitForElement(string automationId, TimeSpan? timeout = null)
            => WaitFor(() => FindInApp(cf => cf.ByAutomationId(automationId)), $"element '{automationId}'", timeout);

         private static T Field<T>(MainWindow window, string name) where T : class
            => typeof(MainWindow).GetField(name)?.GetValue(window) as T ?? throw new InvalidOperationException(name);
         """);
      List<string> unreadable = [];

      Assert.Equal(["Compared", "Direct", "First", "InALambda", "InAMenu", "Second"], InteractiveSuites.IdentifiersLookedUp([file], unreadable), StringComparer.Ordinal);
      Assert.Empty(unreadable);
      Assert.Equal(["ConnectionDialog", "PrintersList"], InteractiveSuites.ElementNamesLookedUp([file]), StringComparer.Ordinal);
   }



   /// <summary>A lookup whose identifier is a local, or a helper that forwards an identifier and is not itself listed as a lookup, is reported: its identifiers would otherwise go unread.</summary>
   [Theory]
   [InlineData("public void Test() { string name = Pick(); session.WaitForElement(name); }")]
   [InlineData("public void Press(string automationId) => ClickElement(automationId);")]
   [InlineData("public void Test() { foreach(string header in Headers()) { session.WaitForElement(header); } }")]
   [InlineData("public void Test() { string[] headers = [\"One\", other]; foreach(string header in headers) { session.WaitForElement(header); } }")]
   public void Lookup_ThatCannotBeRead_IsReported(string source)
   {
      List<string> unreadable = [];

      SortedSet<string> identifiers = InteractiveSuites.IdentifiersLookedUp([new SourceFile("UiTests/Sample.cs", source)], unreadable);

      Assert.Empty(identifiers);
      Assert.StartsWith("UiTests/Sample.cs:1: the identifier looked up here (", Assert.Single(unreadable), StringComparison.Ordinal);
   }
}
