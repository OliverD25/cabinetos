using System.Xml;
using System.Xml.Linq;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// The window's chrome never takes the keyboard (docs/ui.md, "The top row"). A click on a top-row button, the sidebar's workspace row, a pill, a crumb, a tab
/// strip button, the dock's header or a rail button leaves the keyboard in the pane, so Tab (<c>view.focusOtherPane</c>, context
/// <c>filesView</c>) still switches panes. These tests read the XAML and the code that builds buttons; the end-to-end test in
/// <c>ChromeKeyboardEndToEndTests</c> and the live check prove it on a running window.
/// </summary>
public class ChromeKeyboardTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    // Buttons that keep the keyboard, and why. A name here is x:Name or, without one, the accessible name.
    private static readonly Dictionary<string, string> Exceptions = new()
    {
        ["ToolDock.xaml/ReloadButton"] = "shown only when the terminal's page has stopped; Reload is the one action there, and the reloaded page takes the keyboard",
        ["EditorPane.xaml/ReloadButton"] = "shown only when the tool's page has stopped; Reload is the one action there, and the reloaded page takes the keyboard",
    };

    private static string UiFile(string relative) => Path.Combine(Repo.Root, "ui", "CabinetOS", relative.Replace('/', Path.DirectorySeparatorChar));

    private static IEnumerable<(string Name, string Where, XElement Button)> ButtonsOf(string relative)
    {
        var document = XDocument.Load(UiFile(relative), LoadOptions.SetLineInfo);
        foreach (var button in document.Descendants(Xaml + "Button"))
        {
            var name = (string?)button.Attribute(X + "Name") ?? (string?)button.Attribute("AutomationProperties.Name") ?? "(unnamed)";
            var line = ((IXmlLineInfo)button).LineNumber;
            yield return (name, $"{relative}:{line} {name}", button);
        }
    }

    private static bool IsOff(XElement button, string attribute) => (string?)button.Attribute(attribute) == "False";

    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("Views/PaneCrumbs.xaml")]
    [InlineData("Views/PaneTabs.xaml")]
    [InlineData("Views/ActivityRail.xaml")]
    [InlineData("Views/ToolDock.xaml")]
    [InlineData("Views/EditorPane.xaml")]
    public void Every_button_of_the_chrome_takes_no_keyboard(string relative)
    {
        var file = Path.GetFileName(relative);
        foreach (var (name, where, button) in ButtonsOf(relative))
        {
            if (Exceptions.ContainsKey($"{file}/{name}"))
            {
                continue;
            }
            Assert.True(IsOff(button, "IsTabStop"), $"{where} has no IsTabStop=\"False\"");
            Assert.True(IsOff(button, "AllowFocusOnInteraction"), $"{where} has no AllowFocusOnInteraction=\"False\"");
        }
    }

    [Theory]
    [InlineData("MainWindow.xaml", "MenuButton QuickOpenChip DualButton TerminalButton MarketplaceButton PaletteButton SettingsButton WorkspaceHeader")]
    public void The_shells_controls_are_there_and_are_chrome(string relative, string names)
    {
        // v2 of the shell redesign: the top row's chip and the sidebar's workspace row are chrome too, so the theory above
        // checks them; this one fails when one of them is renamed or gone, so that check cannot pass on nothing.
        var buttons = ButtonsOf(relative).ToDictionary(b => b.Name, b => b.Button);
        foreach (var name in names.Split(' '))
        {
            Assert.True(buttons.TryGetValue(name, out var button), $"{relative} has no button {name}");
            Assert.True(IsOff(button, "IsTabStop") && IsOff(button, "AllowFocusOnInteraction"), $"{relative} {name} takes the keyboard");
        }
    }

    [Fact]
    public void The_sidebars_rows_take_no_keyboard_from_a_click_but_stay_tab_stops()
    {
        // Ctrl+Shift+E without the rail puts the keyboard on the first pinned folder (Sidebar.FocusFirstRow), which needs a tab stop.
        var buttons = ButtonsOf("Views/Sidebar.xaml").ToList();
        Assert.NotEmpty(buttons);
        foreach (var (_, where, button) in buttons)
        {
            Assert.True(IsOff(button, "AllowFocusOnInteraction"), $"{where} has no AllowFocusOnInteraction=\"False\"");
        }
    }

    [Fact]
    public void The_exceptions_name_buttons_that_exist()
    {
        foreach (var key in Exceptions.Keys)
        {
            var parts = key.Split('/');
            Assert.Contains(ButtonsOf($"Views/{parts[0]}"), b => b.Name == parts[1]);
        }
    }

    [Fact]
    public void The_function_key_bars_style_sets_both()
    {
        var style = XDocument.Load(UiFile("Views/FunctionKeyBar.xaml")).Descendants(Xaml + "Style")
            .Single(s => (string?)s.Attribute(X + "Key") == "FunctionKeyStyle");
        var setters = style.Elements(Xaml + "Setter").ToDictionary(s => (string)s.Attribute("Property")!, s => (string?)s.Attribute("Value"));
        Assert.Equal("False", setters["IsTabStop"]);
        Assert.Equal("False", setters["AllowFocusOnInteraction"]);
    }

    [Theory]
    [InlineData("Views/PaneCrumbs.xaml.cs", true)]
    [InlineData("Views/ToolDock.xaml.cs", true)]
    [InlineData("Views/ActivityRail.xaml.cs", false)]
    public void Every_button_the_code_builds_takes_no_keyboard_from_a_click(string relative, bool alsoNoTabStop)
    {
        // The rail's buttons stay tab stops: Up, Down and Shift+Up/Down work on a button the keyboard was walked to (ActivityRail.FocusButton).
        var source = File.ReadAllText(UiFile(relative));
        var found = 0;
        for (var at = source.IndexOf("new Button", StringComparison.Ordinal); at >= 0; at = source.IndexOf("new Button", at + 1, StringComparison.Ordinal))
        {
            var open = source.IndexOf('{', at);
            var depth = 0;
            var end = open;
            for (; end < source.Length; end++)
            {
                depth += source[end] == '{' ? 1 : source[end] == '}' ? -1 : 0;
                if (depth == 0)
                {
                    break;
                }
            }
            var body = source[open..(end + 1)];
            var line = source[..at].Count(c => c == '\n') + 1;
            Assert.True(body.Contains("AllowFocusOnInteraction = false", StringComparison.Ordinal), $"{relative}:{line} builds a Button that takes the keyboard from a click");
            if (alsoNoTabStop)
            {
                Assert.True(body.Contains("IsTabStop = false", StringComparison.Ordinal), $"{relative}:{line} builds a Button that is a tab stop");
            }
            found++;
        }
        Assert.True(found > 0, $"{relative} builds no Button any more: update this test");
    }
}
