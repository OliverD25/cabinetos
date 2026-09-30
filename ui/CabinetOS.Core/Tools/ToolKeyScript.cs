using System.Globalization;
using System.Text.Json;
using CabinetOS.Core.Keys;

namespace CabinetOS.Core.Tools;

/// <summary>
/// The window's script in every tool page (<c>AddScriptToExecuteOnDocumentCreatedAsync</c>).
/// A focused web page gets every key, and the window sees none; so this
/// script hands the window its ways out (<c>palette.show</c>,
/// <c>view.toggleTerminal</c>) as <c>{"type":"key"}</c> messages, whatever
/// the tool does, the same way the terminal page does. The key names come
/// from the window's own table (<see cref="KeyNames"/>), so a combination
/// means the same on both sides.
/// </summary>
public static class ToolKeyScript
{
    /// <summary>The script, passing on <paramref name="passKeys"/> until a <c>passKeys</c> message changes them.</summary>
    public static string Build(IEnumerable<string> passKeys)
    {
        var names = JsonSerializer.Serialize(KeyNames.VirtualKeyNames
            .OrderBy(pair => pair.Key)
            .ToDictionary(pair => pair.Key.ToString(CultureInfo.InvariantCulture), pair => pair.Value));
        var keys = JsonSerializer.Serialize(passKeys.ToArray());
        return $$"""
            (() => {
              const webview = window.chrome && window.chrome.webview;
              if (!webview || window.__cabinetosKeys) { return; }
              window.__cabinetosKeys = true;
              const names = {{names}};
              let keys = new Set({{keys}});
              webview.addEventListener('message', (event) => {
                try {
                  const message = JSON.parse(event.data);
                  if (message && message.type === 'passKeys') { keys = new Set(message.keys || []); }
                } catch { }
              });
              window.addEventListener('keydown', (event) => {
                const name = names[event.keyCode];
                if (!name) { return; }
                const combo = (event.ctrlKey ? 'ctrl+' : '') + (event.shiftKey ? 'shift+' : '') +
                  (event.altKey ? 'alt+' : '') + (event.metaKey ? 'win+' : '') + name;
                if (keys.has(combo)) {
                  event.preventDefault();
                  event.stopImmediatePropagation();
                  // The keys passed on are the window's toggles and ways out: a key held down runs its command once.
                  if (!event.repeat) {
                    webview.postMessage(JSON.stringify({ type: 'key', keys: combo }));
                  }
                }
              }, true);
            })();
            """;
    }
}
