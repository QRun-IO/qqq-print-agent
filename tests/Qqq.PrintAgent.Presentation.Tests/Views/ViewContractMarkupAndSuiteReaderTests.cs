/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Tests.Views;

/// <summary>
/// The readers of markup, of kinds, of generated forms and of the interactive suites, tried on small pieces of source
/// written here, as ViewContractReaderTests tries the readers of the companion's C#.
/// </summary>
public sealed class ViewContractMarkupAndSuiteReaderTests
{
   private const string Namespaces = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

   /// <summary>A class that creates controls in each place a control can be created in code; ControlsCreatedInCode_AreHeldToTheBlockThatCreatesThem says which of them the contract reports.</summary>
   private const string ControlsCreatedInCode = """
      internal sealed class Sample : Grid
      {
         private readonly Button _once = new();
         private Button _twice = new();
         private readonly Func<Button> _make = () => { Button button = new(); return button; };

         internal Sample(bool wide)
         {
            AutomationProperties.SetAutomationId(_once, "Once");
            AutomationProperties.SetAutomationId(_twice, "Twice");
            if(wide)
            {
               Button open = new();
               AutomationProperties.SetAutomationId(open, "OpenWide");
            }
            else
            {
               Button open = new();
            }
            Button copy = new();
            AutomationProperties.SetAutomationId(copy, "Copy");
            copy = new Button();
            Button late;
            if(wide)
            {
               late = new Button();
               AutomationProperties.SetAutomationId(late, "Late");
            }
            late = new Button();
         }

         internal Button Extra
         {
            get
            {
               Button button = new();
               return button;
            }
         }

         internal Button Named
         {
            get
            {
               Button named = new();
               AutomationProperties.SetAutomationId(named, "Named");
               return named;
            }
         }

         private Button Add(string id)
         {
            Button button = new();
            AutomationProperties.SetAutomationId(button, id);
            return button;
         }

         private void Rebuild() => _twice = new Button();

         private void Lone()
         {
            Button alone = new();
         }
      }
      """;



   /// <summary>Markup is read element by element: a property element is not one, the language's own elements are marked, and a text that follows a one-way binding is seen to change while one that is bound once is not.</summary>
   [Fact]
   public void Markup_IsReadWithNamesIdentifiersLiveRegionsAndBindings()
   {
      SourceFile file = new("Views/Sample.xaml", $$"""
         <Page x:Class="Sample.Views.SamplePage" {{Namespaces}}>
            <Page.Resources><x:Double x:Key="Width">760</x:Double></Page.Resources>
            <Button x:Name="Save" Content="Save changes" AutomationProperties.AutomationId="SaveChanges" />
            <TextBlock x:Name="Result" Text="{x:Bind Model.Result, Mode=OneWay}" AutomationProperties.LiveSetting="Polite" />
            <TextBlock Text="{x:Bind Model.Title}" />
            <InfoBar Message="{Binding Message}" />
            <TextBlock Text="{x:Bind Model.Spaced, Mode = OneWay}" />
            <TextBlock Name="Plain" Text="{x:Bind Model.Once, Mode = OneTime}" />
         </Page>
         """);

      IReadOnlyList<ViewMarkup.Element> elements = ViewMarkup.Elements(file);

      Assert.Equal(["Page", "x:Double", "Button", "TextBlock", "TextBlock", "InfoBar", "TextBlock", "TextBlock"], elements.Select(element => element.Kind), StringComparer.Ordinal);
      Assert.Equal(new ViewMarkup.Element("Views/Sample.xaml:3 <Button> \"Save changes\"", "Button", "Save", "SaveChanges", null, Bound: false, "<Button> Save", TakesInput: false), elements[2]);
      Assert.Equal(new ViewMarkup.Element("Views/Sample.xaml:4 <TextBlock> \"{x:Bind Model.Result, Mode=OneWay}\"", "TextBlock", "Result", null, "Polite", Bound: true, "<TextBlock> Result", TakesInput: false), elements[3]);
      Assert.Equal([false, false, false, true, false, true, true, false], elements.Select(element => element.Bound));
      Assert.Equal(("Plain", "<TextBlock> Plain"), (elements[7].Name, elements[7].Control));
      Assert.Equal("SamplePage", ViewMarkup.ClassOf(file));
      Assert.Equal("SamplePage", ViewMarkup.OwnerOf(file));
      Assert.Null(ViewMarkup.ClassOf(new SourceFile("Loose.xaml", $"<Grid {Namespaces} />")));
      Assert.Equal("Loose.xaml", ViewMarkup.OwnerOf(new SourceFile("Loose.xaml", $"<Grid {Namespaces} />")));
   }



   /// <summary>
   /// A control is described without a line number: by its tag and <c>x:Name</c>, or, without a name, by its tag, its
   /// label and the nearest named element around it, so two buttons with the same label on two pages are told apart.
   /// An element takes input when it has a handler for a press or a key, or is a tab stop.
   /// </summary>
   [Fact]
   public void Markup_DescribesAControlAndSeesWhatTakesInput()
   {
      SourceFile file = new("Sample.xaml", $"""
         <Grid {Namespaces}>
            <StackPanel x:Name="LogsPage"><Button Content="Log settings" Click="Open" /><Grid><Button Content="Clear" /></Grid></StackPanel>
            <StackPanel x:Name="SettingsPage"><Button Content="Log settings" /><Border Tapped="Open" /><Border IsTabStop="True" /><Border IsTabStop="False" Loaded="Ready" /></StackPanel>
            <MenuBar />
         </Grid>
         """);

      IReadOnlyList<ViewMarkup.Element> elements = ViewMarkup.Elements(file);

      Assert.Equal(
         ["<Grid>", "<StackPanel> LogsPage", "<Button> \"Log settings\" in LogsPage", "<Grid> in LogsPage", "<Button> \"Clear\" in LogsPage", "<StackPanel> SettingsPage", "<Button> \"Log settings\" in SettingsPage", "<Border> in SettingsPage", "<Border> in SettingsPage", "<Border> in SettingsPage", "<MenuBar>"],
         elements.Select(element => element.Control), StringComparer.Ordinal);
      Assert.Equal([false, false, true, false, false, false, false, true, true, false, false], elements.Select(element => element.TakesInput));
   }



   /// <summary>A style that sets an automation identifier is found by its setter, whether it names the property or a target's property; a setter of anything else is not.</summary>
   [Fact]
   public void Markup_FindsAnIdentifierSetByAStyle()
   {
      SourceFile file = new("Sample.xaml", $"""
         <Grid {Namespaces}>
            <Grid.Resources>
               <Style x:Key="Row" TargetType="ListViewItem">
                  <Setter Property="Padding" Value="8,0" />
                  <Setter Property="AutomationProperties.AutomationId" Value="PrinterRow" />
               </Style>
            </Grid.Resources>
            <VisualState><VisualState.Setters><Setter Target="Save.(AutomationProperties.AutomationId)" Value="SaveWide" /></VisualState.Setters></VisualState>
            <Button x:Name="Save" AutomationProperties.AutomationId="Save" />
         </Grid>
         """);

      Assert.Equal(["Sample.xaml:5 <Setter>", "Sample.xaml:8 <Setter>"], ViewMarkup.IdentifiersSetByAStyle(file), StringComparer.Ordinal);
   }



   /// <summary>An <c>{x:Bind}</c> that names no mode follows the mode its element, or one around it, sets as the default, and is read once where none does; a mode written on the binding wins.</summary>
   [Fact]
   public void Markup_HonoursTheDefaultBindMode()
   {
      SourceFile file = new("Sample.xaml", $$"""
         <Grid {{Namespaces}}>
            <TextBlock Text="{x:Bind Model.Once}" />
            <StackPanel x:DefaultBindMode="OneWay">
               <TextBlock Text="{x:Bind Model.Follows}" />
               <TextBlock Text="{x:Bind Model.Fixed, Mode=OneTime}" />
               <Grid x:DefaultBindMode="OneTime"><TextBlock Text="{x:Bind Model.Inner}" /></Grid>
            </StackPanel>
            <TextBlock x:DefaultBindMode="TwoWay" Text="{x:Bind Model.Own}" />
         </Grid>
         """);

      Assert.Equal([false, true, false, false, true], ViewMarkup.Elements(file).Where(element => string.Equals(element.Kind, "TextBlock", StringComparison.Ordinal)).Select(element => element.Bound));
   }



   /// <summary>A view's code-behind is the files in its folder that start with its name and a dot, and no other view's.</summary>
   [Fact]
   public void CodeBehind_IsTheFilesThatStartWithTheViewsName()
   {
      SourceFile[] code = [new("MainWindow.xaml.cs", ""), new("MainWindow.Jobs.cs", ""), new("MainWindowHelper.cs", ""), new("Views/MainWindow.xaml.cs", ""), new("JobRowView.cs", "")];

      Assert.Equal(["MainWindow.xaml.cs", "MainWindow.Jobs.cs"], CompanionSources.CodeBehindOf(new SourceFile("MainWindow.xaml", ""), code).Select(file => file.Name), StringComparer.Ordinal);
      Assert.Equal(["Views/MainWindow.xaml.cs"], CompanionSources.CodeBehindOf(new SourceFile("Views/MainWindow.xaml", ""), code).Select(file => file.Name), StringComparer.Ordinal);
   }



   /// <summary>
   /// A kind is what the lists say, and a class of the companion is what it derives from, however far: interactive
   /// through another class, a view when it has its own markup, nothing at all when it derives from nothing or from an
   /// interface, and unknown when it derives from a kind that is in no list. A class whose file sets an identifier on
   /// <c>this</c> names itself.
   /// </summary>
   [Fact]
   public void Kinds_AreTheListsAndWhatACompanionClassDerivesFrom()
   {
      SourceFile[] code =
      [
         new("Controls.cs", """
            internal class HealthButton : Button { }
            internal sealed class RowView : Grid { }
            internal sealed class Log : ILog { }
            internal sealed class Plain { }
            internal sealed class Tip : TeachingTip { }
            internal sealed class First : Second { }
            internal sealed class Second : First { }
            """),
         new("LoudButton.cs", "internal sealed record Volume(int Level); internal sealed class LoudButton : HealthButton { internal LoudButton() { AutomationProperties.SetAutomationId(this, \"Loud\"); } } internal sealed class QuietButton : HealthButton { }"),
         new("Views/WizardView.xaml.cs", "public sealed partial class WizardView : UserControl { }"),
         new("Views/WizardView.Steps.cs", "public sealed partial class WizardView { }"),
      ];
      SourceFile[] markup = [new("Views/WizardView.xaml", $"<UserControl x:Class=\"Sample.Views.WizardView\" {Namespaces} />"), new("Views/PickerView.xaml", $"<Button x:Class=\"Sample.Views.PickerView\" {Namespaces} />")];
      ControlKinds kinds = new(Set("Button"), Set("TextBlock"), Set("Grid", "UserControl"), Set("Uri"), markup, [.. code, new SourceFile("Views/PickerView.xaml.cs", "public sealed partial class PickerView : Button { }")]);

      Assert.Equal(
         [ControlKinds.Sort.Interactive, ControlKinds.Sort.Text, ControlKinds.Sort.Passive, ControlKinds.Sort.NotAnElement, ControlKinds.Sort.Unknown],
         new[] { "Button", "TextBlock", "Grid", "Uri", "TeachingTip" }.Select(kinds.Of));
      Assert.Equal(
         [ControlKinds.Sort.Interactive, ControlKinds.Sort.Interactive, ControlKinds.Sort.Passive, ControlKinds.Sort.NotAnElement, ControlKinds.Sort.NotAnElement, ControlKinds.Sort.Unknown, ControlKinds.Sort.Passive, ControlKinds.Sort.Interactive, ControlKinds.Sort.NotAnElement],
         new[] { "HealthButton", "LoudButton", "RowView", "Log", "Plain", "Tip", "WizardView", "PickerView", "First" }.Select(kinds.Of));
      Assert.True(kinds.NamesItself("LoudButton"));
      Assert.Equal([false, false, false], new[] { "RowView", "QuietButton", "Volume" }.Select(kinds.NamesItself));
      Assert.Equal(ControlKinds.Sort.Passive, new ControlKinds(Set(), Set(), Set(), Set(), markup, []).Of("WizardView"));
   }



   /// <summary>
   /// A control created in code is held to the place it is one control in: a local to the block that creates it, a
   /// field to its file. So the second branch of an <c>if</c>, an accessor and a lambda each need their own
   /// <c>SetAutomationId</c>, however the method before them names a local of the same name; and two controls
   /// assigned to one name in one block, or to one field, are reported once, whether or not a call names them.
   /// </summary>
   [Fact]
   public void ControlsCreatedInCode_AreHeldToTheBlockThatCreatesThem()
   {
      SourceFile file = new("Sample.cs", ControlsCreatedInCode);
      ControlKinds kinds = new(Set("Button"), Set(), Set("Grid"), Set(), [], [file]);

      string[] problems = [.. XamlContractTests.CreatedWithoutAnIdentifier(kinds, file, []).Distinct(StringComparer.Ordinal)];

      string[] expected =
         [
            "Sample.cs:4: 2 controls are assigned to _twice in one file, where it is a field or a property (Sample.cs:4 and Sample.cs:58).",
            "Sample.cs:5: this Button is created in code, and no SetAutomationId in the block that creates it (the braces around this line) names button.",
            "Sample.cs:18: this Button is created in code, and no SetAutomationId in the block that creates it (the braces around this line) names open.",
            "Sample.cs:20: 2 controls are assigned to copy in one block (Sample.cs:20 and Sample.cs:22).",
            "Sample.cs:26: 2 controls are assigned to late in one block (Sample.cs:26 and Sample.cs:29).",
            "Sample.cs:36: this Button is created in code, and no SetAutomationId in the block that creates it (the braces around this line) names button. What names button at Sample.cs:54 is outside that block, in another branch, another method or an accessor, and so names another control.",
            "Sample.cs:62: this Button is created in code, and no SetAutomationId names alone. Add AutomationProperties.SetAutomationId(alone, \"Identifier\") in the block that creates it (the braces around this line)",
         ];

      Assert.Equal(expected.Length, problems.Length);
      Assert.All(expected.Zip(problems), pair => Assert.StartsWith(pair.First, pair.Second, StringComparison.Ordinal));
      Assert.Contains("If they are different controls, give each a variable or a field of its own and its own AutomationProperties.SetAutomationId, and record each identifier. If it is one control, create it in one place.", problems[0], StringComparison.Ordinal);
   }



   /// <summary>
   /// A name given at run time fails on what is, or may be, an element: the class the line is in when that is one, a
   /// receiver whose type no list knows, and a receiver whose type the file does not say; each with what to do. What
   /// is known to be no element (a class of the companion that derives from nothing, a listed value) is left alone.
   /// </summary>
   [Fact]
   public void NamesGivenAtRunTime_FailOnWhatIsOrMayBeAnElement()
   {
      SourceFile file = new("Sample.cs", """
         internal sealed class Row : Grid
         {
            internal Row(Job job, Profile profile, Plain plain)
            {
               Name = $"JobRow{job.JobId}";
               profile.Name = job.Title;
               plain.Name = job.Title;
               job.Owner.Name = job.Title;
               Uri address = new(job.Link) { Name = job.Title };
            }
         }

         internal sealed class Plain
         {
            internal Plain(string title) => Name = title;
         }
         """);
      ControlKinds kinds = new(Set("Button"), Set("TextBlock"), Set("Grid"), Set("Uri"), [], [file]);

      string[] problems = [.. XamlContractTests.NamesGivenToElementsAtRunTime(kinds, [file])];

      Assert.Equal(3, problems.Length);
      Assert.StartsWith("Sample.cs:5: Name is given a value that is not written out ($\"JobRow{job.JobId}\") on this Row. WinUI reports an element's Name as its automation identifier when it has no other", problems[0], StringComparison.Ordinal);
      Assert.EndsWith("Do not name an element from a value at run time.", problems[0], StringComparison.Ordinal);
      Assert.StartsWith("Sample.cs:6: Name is given a value that is not written out (job.Title) on profile (Profile).", problems[1], StringComparison.Ordinal);
      Assert.EndsWith("If Profile is no element of a view, add it to s_notElements in XamlContractTests.Kinds.cs.", problems[1], StringComparison.Ordinal);
      Assert.StartsWith("Sample.cs:8: Name is given a value that is not written out (job.Title) on an expression, whose type the contract cannot read here.", problems[2], StringComparison.Ordinal);
      Assert.Contains("create it with new Type { ... }, or declare the variable in this file with its type", problems[2], StringComparison.Ordinal);
   }



   /// <summary>A set of kinds, compared as the contract compares them.</summary>
   private static HashSet<string> Set(params string[] kinds) => new(kinds, StringComparer.Ordinal);



   /// <summary>A generated form covers its stem, a dot and a position from one, and nothing else.</summary>
   [Theory]
   [InlineData("Server.1", true)]
   [InlineData("Server.12", true)]
   [InlineData("Server.0", false)]
   [InlineData("Server.01", false)]
   [InlineData("Server.", false)]
   [InlineData("Server", false)]
   [InlineData("Server.1a", false)]
   [InlineData("Servers.1", false)]
   [InlineData("Printer.1", false)]
   public void GeneratedForm_CoversAStemADotAndAPosition(string identifier, bool covered)
   {
      GeneratedForm form = new("Server", "A server's node.", ProducedAt: null);

      Assert.Equal("Server.<n>", form.Form);
      Assert.Equal(covered, form.Covers(identifier));
   }



   /// <summary>
   /// An identifier that is not written out is allowed only at the place a form names, and a form that names a place
   /// is produced there, once. A form that is not produced yet is neither written by the companion nor looked up by a
   /// suite, and each failure says what to do.
   /// </summary>
   [Fact]
   public void GeneratedForms_HoldWhatIsNotWrittenOutToTheirTable()
   {
      GeneratedForm server = new("Server", "A server's node.", ProducedAt: "Views/TreeView.xaml | <TreeViewItem> in Tree | {x:Bind AutomationId}");
      GeneratedForm job = new("Job", "A job's row.", ProducedAt: null);
      (string Where, string Place) node = ("Views/TreeView.xaml:9 <TreeViewItem>", "Views/TreeView.xaml | <TreeViewItem> in Tree | {x:Bind AutomationId}");
      (string Where, string Place) row = ("JobRowView.cs:40", "JobRowView.cs | this | row.AutomationId");
      SourceFile code = new("JobRowView.cs", "void Name(int index) { string id = $\"Job.{index}\"; string other = \"Jobs.\"; string server = \"Server.1\"; }");

      Assert.Empty(GeneratedForm.Problems([server, job], [node], [], ["Server.3", "NavigateJobs"]));
      Assert.StartsWith("JobRowView.cs:40: this identifier is not written out (JobRowView.cs | this | row.AutomationId), so it is made while the companion runs. Only the generated forms may be (Server.<n>, Job.<n>)", Assert.Single(GeneratedForm.Problems([server, job], [node, row], [], [])), StringComparison.Ordinal);
      Assert.Contains("set that form's ProducedAt to \"JobRowView.cs | this | row.AutomationId\"", Assert.Single(GeneratedForm.Problems([server, job], [node, row], [], [])), StringComparison.Ordinal);
      Assert.StartsWith("Server.<n> is listed as produced at \"Views/TreeView.xaml | <TreeViewItem> in Tree | {x:Bind AutomationId}\", and 0 places", Assert.Single(GeneratedForm.Problems([server, job], [], [], [])), StringComparison.Ordinal);
      Assert.StartsWith("Server.<n> is listed as produced at", Assert.Single(GeneratedForm.Problems([server, job], [node, node], [], [])), StringComparison.Ordinal);
      Assert.StartsWith("JobRowView.cs:1: this writes an identifier of the form Job.<n>, which the table of generated forms marks as not produced yet.", Assert.Single(GeneratedForm.Problems([server, job], [node], [code], [])), StringComparison.Ordinal);
      Assert.StartsWith("Job.2 is looked up by the UI tests or the layout harness. It is of the form Job.<n>, which the table of generated forms in XamlContractTests marks as not produced yet", Assert.Single(GeneratedForm.Problems([server, job], [node], [], ["Job.2", "Server.1"])), StringComparison.Ordinal);
   }



   /// <summary>The suites' lookups are read from a literal, from each position a lookup takes its identifier at, from a loop over an array of literals, from inside a lambda and from a comparison; a helper that forwards its parameter to a lookup adds nothing itself; and an element name is read from a literal or a nameof.</summary>
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
            Button signIn = Field<Button>(window, nameof(MainWindow.BrowserSignInButton));
         }

         public void ClickElement(string automationId) => Invoke(WaitForElement(automationId));

         public void InvokeMenuItem(string menuName, string itemAutomationId) => Invoke(OpenMenuAndFindItem(menuName, itemAutomationId), $"menu item '{itemAutomationId}'");

         public AutomationElement WaitForElement(string automationId, TimeSpan? timeout = null)
            => WaitFor(() => FindInApp(cf => cf.ByAutomationId(automationId)), $"element '{automationId}'", timeout);

         private static T Field<T>(MainWindow window, string name) where T : class
            => typeof(MainWindow).GetField(name)?.GetValue(window) as T ?? throw new InvalidOperationException(name);
         """);
      List<string> unreadable = [];

      Assert.Equal(["Compared", "Direct", "First", "InALambda", "InAMenu", "Second"], InteractiveSuites.IdentifiersLookedUp([file], unreadable), StringComparer.Ordinal);
      Assert.Equal(["BrowserSignInButton", "ConnectionDialog", "PrintersList"], InteractiveSuites.ElementNamesLookedUp([file], unreadable), StringComparer.Ordinal);
      Assert.Empty(unreadable);
   }



   /// <summary>
   /// FlaUI's own lookups find by identifier when their one argument is a string: a literal is read, and so is a loop
   /// over literals. With a condition they are left to the lookups inside it: a lambda, a call that makes a
   /// condition, a parameter declared with another type, and no argument at all.
   /// </summary>
   [Fact]
   public void FlaUiLookups_AreReadWhenGivenAString_AndLeftAloneWhenGivenACondition()
   {
      SourceFile file = new("UiTests/Sample.cs", """
         public void Test(Func<ConditionFactory, ConditionBase> condition, ConditionBase ready)
         {
            window.FindFirstDescendant("ByDescendant");
            row.FindFirstChild("ByChild");
            window.FindAllDescendants("AllDescendants");
            window.FindAllChildren("AllChildren");
            string[] cells = ["FirstCell", "SecondCell"];
            foreach(string cell in cells)
            {
               row.FindFirstChild(cell);
            }
            window.FindFirstDescendant(condition);
            window.FindFirstDescendant(ready);
            window.FindFirstDescendant(cf => cf.ByName("Refresh"));
            window.FindFirstDescendant((cf) => cf.ByName("=> Refresh"));
            window.FindFirstDescendant(cf.ByControlType(ControlType.ListItem));
            window.FindFirstChild(new PropertyCondition(property, "Value"));
            window.FindFirstDescendant();
            window.FindAllChildren(cf => cf.ByAutomationId("InALambda"));
         }
         """);
      List<string> unreadable = [];

      Assert.Equal(["AllChildren", "AllDescendants", "ByChild", "ByDescendant", "FirstCell", "InALambda", "SecondCell"], InteractiveSuites.IdentifiersLookedUp([file], unreadable), StringComparer.Ordinal);
      Assert.Empty(unreadable);
   }



   /// <summary>
   /// What a FlaUI lookup is given that is a string and no literal, or that cannot be told from a string, is reported
   /// with both ways out: an interpolation, a sum, a string parameter of a helper that is no lookup, a local declared
   /// with <c>var</c>, a name the method does not declare, a member of something, an element of an array, a cast, and
   /// a call that is known to give a string.
   /// </summary>
   [Theory]
   [InlineData("public void Test(int row) { window.FindFirstDescendant($\"Row{row}\"); }")]
   [InlineData("public void Test() { window.FindFirstChild(\"Row\" + suffix); }")]
   [InlineData("public void Test() { window.FindFirstChild(prefix + \"Row\"); }")]
   [InlineData("public AutomationElement Find(string id) => window.FindFirstDescendant(id);")]
   [InlineData("public void Test() { var id = Pick(); window.FindFirstDescendant(id); }")]
   [InlineData("public void Test() { window.FindFirstDescendant(RowId); }")]
   [InlineData("public void Test() { window.FindFirstDescendant(Ids.Row); }")]
   [InlineData("public void Test() { window.FindAllChildren(ids[0]); }")]
   [InlineData("public void Test() { window.FindFirstChild((string)boxed); }")]
   [InlineData("public void Test() { window.FindFirstDescendant(nameof(MainWindow.RefreshButton)); }")]
   [InlineData("public void Test() { window.FindFirstDescendant(row.Id.ToString()); }")]
   [InlineData("public void Test() { window.FindFirstDescendant(string.Concat(prefix, suffix)); }")]
   [InlineData("public void Test() { window.FindFirstChild(\"No=>SuchThing\" + suffix); }")]
   public void FlaUiLookup_ThatMayBeGivenAStringThatCannotBeRead_IsReported(string source)
   {
      List<string> unreadable = [];

      SortedSet<string> identifiers = InteractiveSuites.IdentifiersLookedUp([new SourceFile("UiTests/Sample.cs", source)], unreadable);

      Assert.Empty(identifiers);
      Assert.StartsWith("UiTests/Sample.cs:1: the identifier looked up here (", Assert.Single(unreadable), StringComparison.Ordinal);
      Assert.Contains("finds by automation identifier when it is given a string. If this is a condition and no string, write it as a lambda (cf => ...), or declare it in this method with its type", unreadable[0], StringComparison.Ordinal);
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



   /// <summary>A lookup by identifier in a form the reader does not know is reported, not skipped: an XPath, an element's AutomationId property, an identifier compared with ==, and a lookup handed over as a delegate.</summary>
   [Theory]
   [InlineData("public void Test() { window.FindFirstByXPath(\"//Button[@AutomationId='Unseen']\"); }", "AutomationId")]
   [InlineData("public void Test() { window.FindAllDescendants().First(element => element.AutomationId == \"Unseen\"); }", "AutomationId")]
   [InlineData("public void Test() { window.FindAllDescendants().First(element => element.Properties.AutomationId.Value == \"Unseen\"); }", "AutomationId")]
   [InlineData("public void Test() { buttons.Single(button => AutomationProperties.GetAutomationId(button) == \"Unseen\"); }", "GetAutomationId")]
   [InlineData("public void Test() { Func<string, ConditionBase> by = cf.ByAutomationId; }", "ByAutomationId")]
   public void Lookup_InAFormTheReaderDoesNotKnow_IsReported(string source, string word)
   {
      List<string> unreadable = [];

      SortedSet<string> identifiers = InteractiveSuites.IdentifiersLookedUp([new SourceFile("UiTests/Sample.cs", source)], unreadable);

      Assert.Empty(identifiers);
      Assert.StartsWith($"UiTests/Sample.cs:1: {word} is used here outside a lookup the contract reads", Assert.Single(unreadable), StringComparison.Ordinal);
   }



   /// <summary>An element name that is a variable, or is put together, is reported, and the harness's own declaration of its helper is not a lookup.</summary>
   [Theory]
   [InlineData("void Test() { var list = root.FindName(name); }")]
   [InlineData("void Test() { Button button = Field<Button>(window, \"Browser\" + suffix); }")]
   [InlineData("void Test() { Button button = Field<Button>(window); }")]
   public void ElementName_ThatCannotBeRead_IsReported(string source)
   {
      List<string> unreadable = [];

      SortedSet<string> names = InteractiveSuites.ElementNamesLookedUp([new SourceFile("LayoutTests/Sample.cs", source)], unreadable);

      Assert.Empty(names);
      Assert.StartsWith("LayoutTests/Sample.cs:1: the element name looked up here (", Assert.Single(unreadable), StringComparison.Ordinal);
   }
}
