# UI map: CabinetOS

Updated: 2026-11-08. Exact labels are in **bold**, copied from the code.
Before telling the user where to click, find the label here. A label that
is missing here: grep the code, then add it here in the same commit.
Language: English only. In labels, {sign} is the greater-than sign (>). CabinetOS has no graphical Settings view yet (Phase 11
of the plan); settings are the file `cabinetos.json`, the menus below and the
command palette.

## Main window
Opened by: starting `CabinetOS.exe`. The window title is **CabinetOS**.
- Top row, left to right: the menu button (tooltip **Menu**), the title, the
  Quick Open chip (tooltip **Quick Open (Ctrl+P) · type {sign} for commands**), then
  the view buttons.
- View buttons (tooltips): **Toggle dual pane (Ctrl+Shift+D)**,
  **Toggle terminal (Ctrl+`)**, **Extensions (Ctrl+Shift+X)**,
  **Command palette (Ctrl+Shift+P)**, **Settings (Ctrl+,)**.
- **Settings (Ctrl+,)** opens `cabinetos.json` in the editor program that F4
  uses. It is not a settings window.
- Pane toolbar tooltips: **Back (Alt+Left)**, **Forward (Alt+Right)**,
  **Up (Backspace)**, **Find in pane (Ctrl+F)**.
- Status bar: **hidden** shows while hidden files are listed.

## Top row menu
Opened by: the menu button (hamburger) at the left of the top row. Each row
shows its first key.
- **New Tab**, **New Folder**, **Find in Pane**, **Go to Path…**,
  **Toggle Sidebar**.
- **Layout**: a submenu with **Classic Layout**, **Terminal on the Right**,
  **Activity Rail**. The current one is checked.
- **Show Hidden Files**: checked while hidden files are shown.
- **Follow the Active Pane**: checked while the Explorer tree follows the
  active pane.
- **Browse Extensions**, **Open Keyboard Shortcuts**.
- **Check for Updates**: shown when no update waits.
- **Restart to Update ({version})**: shown instead, once an update is
  downloaded.

## Function key bar
Opened by: always at the bottom of the window, when the theme turns it on.
- **View** (F3), **Edit** (F4), **Copy** (F5), **Move** (F6), **Mkdir** (F7),
  **Delete** (F8), **Drv** (Alt+F1).

## Command palette
Opened by: `Ctrl+Shift+P`, or the palette button in the top row. The search
field says **Type a command or search**. Rows are shown as `Category: Title`.
Quick Open shows the same rows when you type `>` first.
- Settings and files: **Open Settings**, **Open Keyboard Shortcuts**,
  **Color Theme**, **Choose Editor**, **Edit Context Menu…**,
  **Toggle Windows' Shell Menu**, **Change Keys of Selected Command**.
- View: **Toggle Dual Pane**, **Toggle Integrated Terminal**,
  **Toggle Sidebar**, **Toggle Compact Overlay**, **Toggle Quick View**,
  **Toggle Hidden Files**, **Toggle Column View**, **Toggle Folder Sizes**,
  **Swap Panes**, **Refresh**, **Next Layout**, **Show Explorer**,
  **Show Search**, **Fit Columns to Content**, **Reset Column Widths**,
  **Equal Panes**, **Sort by Name**, **Sort by Extension**,
  **Sort by Date Modified**, **Sort by Size**.
- File: **Copy to Other Pane**, **Move to Other Pane**, **Rename**,
  **Delete**, **Delete Permanently**, **Properties**,
  **New Text File**, **Calculate Folder Size**.
- Go: **Back**, **Forward**, **Up One Level**, **Up to Root**,
  **Go to Path…**, **Quick Open…**, **Pinned Folders…**.
- Tab: **New Tab**, **Close Tab**, **Next Tab**, **Previous Tab**,
  **Toggle Tab Lock**, **Open Folder in New Tab**, **Move Tab to Other Pane**.
- Terminal: **New Terminal**, **Show Terminal**, **Close Terminal**,
  **Default Profile**, **Split Terminal Under the Panes**.
- Update: **Check for Updates**, **Restart to Update**,
  **Roll Back to the Previous Version**, **Show Release Notes**,
  **Toggle Automatic Check**, **Toggle Automatic Install**, **Channel**.
- Diagnostics: **Toggle Heavy Logging**, **Open Log Folder**,
  **Save Log Bundle**, **Log Level**.
- Other: **About CabinetOS** (Help), **New Window**, **Browse Extensions**,
  **Show Plugins**, **Switch Workspace…**.

## Help → About
Opened by: Command palette → **About CabinetOS**.
- Dialog titled **About CabinetOS**. It shows the version, the core, the
  build and the update state, and links to the license and third-party
  notices.
- **Close**: closes the dialog.

## Update dialog and pill
Opened by: it opens by itself when a version is downloaded; or the status-bar
pill; or Command palette → **Check for Updates**.
- **Restart now**: installs the downloaded version and restarts.
- **Later**: closes the dialog. The pill and the menu dot stay.
- **Close**: shown instead of **Later** when no restart is waiting.
- **Release notes in the browser**: link to the changelog.
- Roll back: Command palette → **Roll Back to the Previous Version** asks
  with the title **Roll back to CabinetOS {previous}?**.

## Activity rail and sidebar
Opened by: **Toggle Sidebar** (Ctrl+B), or Command palette → **Activity Rail**
for the rail layout. Rail buttons: **Explorer**, **Search**, **Extensions**,
**Terminal**, then one for each installed tool.
- Explorer lists **PINNED**, **DRIVES** and **FOLDERS**. Right-click a pinned
  folder → **Unpin from sidebar**.
- Tooltips on the Explorer header: **Locate the active folder (Alt+Shift+L)**,
  **Lock the tree: it stops following the active folder**,
  **Follow the active pane: the tree shows the folder of the active pane**.
- Search view: **SEARCH**, field **Search files by name**, switch
  **Whole volume** (search every indexed volume), button
  **Read the index again** (tooltip).

## Pane tabs
Opened by: right-click a tab in the tab row of a pane.
- **Lock tab** / **Unlock tab**, **Move to other pane**, **Close tab**.
- Tooltip of the plus button: **New tab**.

## Quick Open
Opened by: `Ctrl+P`, or the Quick Open chip in the top row.
- Field: **Search files by name (type {sign} for commands)**.
- Hints: **↵ open in this pane**, **Ctrl+↵ open in the other pane**.

## Find in pane
Opened by: `Ctrl+F`, or Command palette → **Find in Pane**.
- Close: **Close find** (Esc).

## Terminal dock
Opened by: `Ctrl+`` (backquote), or the terminal button in the top row, or
Command palette → **Toggle Integrated Terminal**.
- Dock buttons: **New terminal**, **Other shells**, **Hide the terminal**.
- If the terminal fails: **The terminal stopped** with the button
  **Open crash folder**.

## Extensions (marketplace)
Opened by: the **Extensions** rail button, `Ctrl+Shift+X`, or Command palette
→ **Browse Extensions**. Field: **Search extensions**.
- Card footer: **Update available**, **Installed**, **Applied**.
- Detail buttons: **Source**, **Uninstall**. Uninstall asks with the title
  **Uninstall {name}?**.

## Permission review
Opened by: installing an extension that asks for capabilities.
- **Review permissions**, list **REQUESTED CAPABILITIES**.
- **Allow and install**, **Cancel**.

## Theme gallery and picker
Opened by: Command palette → **Browse** (category Themes), or **Color Theme**
for the quick picker.
- Gallery: title **Themes**, field **Search themes**. Filter chips:
  **All**, **Dark**, **Light**, **System**, **Density presets**,
  **Installed**. Right-click a tile → **Remove**.
- Picker: **Color Theme**, field **Search themes**.

## File context menu
Opened by: right-click a file or folder row, or Shift+F10.
- The entries come from `contextMenu` in `cabinetos.json`; edit them with
  Command palette → **Edit Context Menu…**.
- **Open in Terminal**, **Open with…**.
- Shift+right-click shows Windows' own menu instead when the option is on:
  **Show Windows' own menu with Shift+right-click**, or Command palette →
  **Toggle Windows' Shell Menu**.
- Edit mode: buttons **Add Command…** and **Add separator**; **Done** closes it.

## Hotkeys
| Action | Default | Change it in |
|---|---|---|
| Command palette | `Ctrl+Shift+P` | Command palette → **Open Keyboard Shortcuts** |
| Quick Open | `Ctrl+P` | same |
| Settings file | `Ctrl+,` | same |
| Toggle dual pane | `Ctrl+Shift+D` | same |
| Toggle terminal | ``Ctrl+` `` | same |
| Toggle sidebar | `Ctrl+B` | same |
| Find in pane | `Ctrl+F` | same |
| Copy / Move / New folder / Delete | `F5` / `F6` / `F7` / `F8` | same |
