using System.Buffers;
using System.Text.Json;
using CabinetOS.Core.Keys;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Shell;

/// <summary>
/// One row of the edit mode: a row of the target's <c>items</c> as the menu shows it. A command
/// no command has keeps its ID as its title (<see cref="Known"/> false), so it can be removed.
/// <see cref="Detail"/> is the row's extensions filter (".md, .txt"), which the edit mode keeps
/// but does not change.
/// </summary>
public sealed record MenuEditRow(MenuItemConfig Item, string Title, string? Glyph = null, string? Keys = null, string? Badge = null, string? Detail = null, bool Known = true)
{
    /// <summary>Whether the row is a divider.</summary>
    public bool IsSeparator => Item.Separator;
}

/// <summary>What a key does in the edit mode.</summary>
public enum MenuEditAction
{
    /// <summary>Nothing of the edit mode's own: the focused button takes it (Tab, Enter, Space).</summary>
    None,

    /// <summary>Up: the focus to the row above.</summary>
    FocusUp,

    /// <summary>Down: the focus to the row below.</summary>
    FocusDown,

    /// <summary>Alt+Up: the focused row one place up.</summary>
    MoveUp,

    /// <summary>Alt+Down: the focused row one place down.</summary>
    MoveDown,

    /// <summary>Delete: the focused row out.</summary>
    Remove,

    /// <summary>Insert: "Add Command…".</summary>
    AddCommand,

    /// <summary>Ctrl+S: "Done".</summary>
    Save,

    /// <summary>Esc: "Cancel".</summary>
    Cancel,
}

/// <summary>
/// The in-menu edit mode of Phase 18, step 2 (docs/ui.md, "Editing the menu"): the target's
/// <c>items</c> list, reordered, shortened and added to, then written back through the core as
/// <c>set_value contextMenu.&lt;target&gt;.items</c>. Only the list is edited here; the icon row, the
/// extensions filters and <c>programs</c> stay in the file. A row keeps its own
/// <see cref="MenuItemConfig"/> through every move, so its <c>extensions</c> survive untouched. The
/// view only draws this model; every rule is tested without XAML.
/// </summary>
public sealed class ContextMenuEditModel
{
    private readonly List<MenuItemConfig> _items;
    private readonly IReadOnlyList<MenuItemConfig> _original;
    private readonly Dictionary<string, CommandInfo> _byId = new(StringComparer.Ordinal);
    private readonly Func<string, string?> _keysOf;

    /// <summary>
    /// The edit mode of <paramref name="kind"/>'s <paramref name="items"/>, with the registry's
    /// <paramref name="commands"/> for the titles and <paramref name="keysOf"/> for the keys shown.
    /// The first row has the focus.
    /// </summary>
    public ContextMenuEditModel(MenuTargetKind kind, IReadOnlyList<MenuItemConfig> items, IReadOnlyList<CommandInfo> commands, Func<string, string?> keysOf)
    {
        Kind = kind;
        // A row without a command cannot be in a file the core accepted; it could not be written back either.
        _original = [.. items.Where(i => i.Separator || i.Command is { Length: > 0 })];
        _items = [.. _original];
        foreach (var command in commands)
        {
            _byId.TryAdd(command.Id, command);
        }
        Commands = commands;
        _keysOf = keysOf;
        Focus = _items.Count > 0 ? 0 : -1;
    }

    /// <summary>The target being edited.</summary>
    public MenuTargetKind Kind { get; }

    /// <summary>The registry the rows are described from.</summary>
    public IReadOnlyList<CommandInfo> Commands { get; }

    /// <summary>The header's name of the target: "File menu", "Folder menu", "Empty space menu", "Selection menu".</summary>
    public string TargetName => NameOf(Kind);

    /// <summary>The setting "Done" writes: <c>contextMenu.file.items</c> and so on.</summary>
    public string SettingPath => $"contextMenu.{KeyOf(Kind)}.items";

    /// <summary>The list as edited so far.</summary>
    public IReadOnlyList<MenuItemConfig> Items => _items;

    /// <summary>The focused row's index, where "Add Command…" and "Add separator" insert after; -1 when the list is empty.</summary>
    public int Focus { get; private set; }

    /// <summary>Whether the list differs from the one the edit mode started with.</summary>
    public bool IsChanged => !_items.SequenceEqual(_original);

    /// <summary>The rows as the edit mode shows them.</summary>
    public IReadOnlyList<MenuEditRow> Rows => [.. _items.Select(Describe)];

    /// <summary>The rows' titles, "-" for a divider, "|" between them (for the log and the checks).</summary>
    public string Describe() => string.Join("|", _items.Select(i => i.Separator ? "-" : Describe(i).Title));

    /// <summary>The header's name of a target.</summary>
    public static string NameOf(MenuTargetKind kind) => kind switch
    {
        MenuTargetKind.Background => "Empty space menu",
        MenuTargetKind.File => "File menu",
        MenuTargetKind.Folder => "Folder menu",
        _ => "Selection menu",
    };

    /// <summary>A target's key in <c>contextMenu</c>.</summary>
    public static string KeyOf(MenuTargetKind kind) => kind switch
    {
        MenuTargetKind.Background => "background",
        MenuTargetKind.File => "file",
        MenuTargetKind.Folder => "folder",
        _ => "multiSelect",
    };

    /// <summary>
    /// What the menu shows around the list while it is edited: the icon row as it is, and after the
    /// list the plugins' group, Properties and "Edit Menu…", which are not rows of the file.
    /// </summary>
    public static ContextMenuView Fixed(ContextMenuConfig config, ContextMenuFacts facts, IReadOnlyList<CommandInfo> commands, Func<string, string?> keysOf) =>
        ContextMenuModel.Build(config.With(facts.Kind, config.For(facts.Kind) with { Items = [] }), facts, commands, keysOf);

    /// <summary>
    /// The commands "Add Command…" offers, by title: the ones that may sit in a file pane's menu
    /// (the registry's with <c>when</c> <c>filesView</c>, the programs, the plugins'), without the
    /// ones the list has already, and without Properties and "Edit Menu…", which have their own places.
    /// </summary>
    public IReadOnlyList<CommandInfo> Addable()
    {
        var listed = _items.Where(i => i.Command is not null).Select(i => i.Command!).ToHashSet(StringComparer.Ordinal);
        return [.. Commands
            .Where(c => c.When == KeyContexts.FilesView || c.Source.Kind is "program" or "plugin")
            .Where(c => c.Id is not (ContextMenuModel.Properties or ContextMenuModel.EditMenu) && !listed.Contains(c.Id))
            .DistinctBy(c => c.Id)
            .OrderBy(ContextMenuModel.TitleOf, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>The title the menu gives a command (its own for today's entries, else the registry's).</summary>
    public static string TitleOf(CommandInfo command) => ContextMenuModel.TitleOf(command);

    /// <summary>Puts the focus on row <paramref name="index"/>.</summary>
    public void SetFocus(int index)
    {
        if (index >= 0 && index < _items.Count)
        {
            Focus = index;
        }
    }

    /// <summary>Moves the focus by <paramref name="delta"/> rows, within the list; whether it moved.</summary>
    public bool MoveFocus(int delta)
    {
        if (_items.Count == 0)
        {
            return false;
        }
        var next = Math.Clamp(Focus + delta, 0, _items.Count - 1);
        var moved = next != Focus;
        Focus = next;
        return moved;
    }

    /// <summary>Moves row <paramref name="from"/> to place <paramref name="to"/>; the focus goes with it. Whether it moved.</summary>
    public bool Move(int from, int to)
    {
        if (from < 0 || from >= _items.Count)
        {
            return false;
        }
        to = Math.Clamp(to, 0, _items.Count - 1);
        if (to == from)
        {
            return false;
        }
        var item = _items[from];
        _items.RemoveAt(from);
        _items.Insert(to, item);
        Focus = to;
        return true;
    }

    /// <summary>Moves the focused row by <paramref name="delta"/> places (Alt+Up, Alt+Down).</summary>
    public bool MoveFocused(int delta) => Focus >= 0 && Move(Focus, Focus + delta);

    /// <summary>Takes row <paramref name="index"/> out; the focus goes to the row that took its place, or the one above.</summary>
    public bool Remove(int index)
    {
        if (index < 0 || index >= _items.Count)
        {
            return false;
        }
        _items.RemoveAt(index);
        Focus = _items.Count == 0 ? -1 : Math.Min(index, _items.Count - 1);
        return true;
    }

    /// <summary>A divider after the focused row (first in an empty list); it gets the focus.</summary>
    public void InsertSeparator() => Insert(MenuItemConfig.Divider);

    /// <summary>Command <paramref name="id"/> after the focused row (first in an empty list); it gets the focus.</summary>
    public void InsertCommand(string id) => Insert(new MenuItemConfig(id));

    private void Insert(MenuItemConfig item)
    {
        var at = Focus + 1;
        _items.Insert(at, item);
        Focus = at;
    }

    /// <summary>
    /// The list as <c>set_value</c> takes it, in the shape of the defaults: <c>{"command": …}</c>, with
    /// <c>"extensions": […]</c> when the row has them, or <c>{"separator": true}</c>.
    /// </summary>
    public JsonElement ToValue()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var item in _items)
            {
                writer.WriteStartObject();
                if (item.Separator)
                {
                    writer.WriteBoolean("separator", true);
                }
                else
                {
                    writer.WriteString("command", item.Command);
                    if (item.Extensions is { } extensions)
                    {
                        writer.WriteStartArray("extensions");
                        foreach (var extension in extensions)
                        {
                            writer.WriteStringValue(extension);
                        }
                        writer.WriteEndArray();
                    }
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>What a key does in the edit mode (Article 7: the mode works without the mouse).</summary>
    public static MenuEditAction ActionFor(KeyCombo combo) => (combo.Modifiers, combo.Key) switch
    {
        (KeyModifiers.None, "up") => MenuEditAction.FocusUp,
        (KeyModifiers.None, "down") => MenuEditAction.FocusDown,
        (KeyModifiers.Alt, "up") => MenuEditAction.MoveUp,
        (KeyModifiers.Alt, "down") => MenuEditAction.MoveDown,
        (KeyModifiers.None, "delete") => MenuEditAction.Remove,
        (KeyModifiers.None, "insert") => MenuEditAction.AddCommand,
        (KeyModifiers.Ctrl, "s") => MenuEditAction.Save,
        (KeyModifiers.None, "escape") => MenuEditAction.Cancel,
        _ => MenuEditAction.None,
    };

    private MenuEditRow Describe(MenuItemConfig item)
    {
        if (item.Separator)
        {
            return new MenuEditRow(item, "Separator");
        }
        var id = item.Command!;
        var detail = item.Extensions is { Count: > 0 } extensions ? string.Join(", ", extensions) : null;
        if (!_byId.TryGetValue(id, out var command))
        {
            return new MenuEditRow(item, id, Detail: detail ?? "no command has this ID", Known: false);
        }
        return new MenuEditRow(item, ContextMenuModel.TitleOf(command), ContextMenuModel.GlyphOf(id),
            ContextMenuModel.ShowsKeys(id) ? _keysOf(id) : null, ContextMenuModel.Badge(command), detail);
    }
}
