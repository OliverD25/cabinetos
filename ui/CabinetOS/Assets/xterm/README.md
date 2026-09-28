# The terminal page

The terminal pane draws with [xterm.js](https://xtermjs.org/) inside
WebView2 ([docs/ui.md](../../../../docs/ui.md), "The terminal"). Everything
here ships next to `CabinetOS.exe` and is served from the virtual host
`https://terminal.cabinetos.example/`; no file comes from a CDN.

| File | What | Source |
|---|---|---|
| `terminal.html`, `terminal.css`, `terminal.js` | The page: one xterm.js terminal per session, and the messages to and from the window | This repository |
| `xterm/xterm.js`, `xterm/xterm.css`, `xterm/LICENSE` | @xterm/xterm 6.0.0, MIT | `lib/xterm.js`, `css/xterm.css` and `LICENSE` of https://registry.npmjs.org/@xterm/xterm/-/xterm-6.0.0.tgz, unchanged |
| `addon-fit/addon-fit.js`, `addon-fit/LICENSE` | @xterm/addon-fit 0.11.0, MIT | `lib/addon-fit.js` and `LICENSE` of https://registry.npmjs.org/@xterm/addon-fit/-/addon-fit-0.11.0.tgz, unchanged |

SHA-256 of the copied files, to check an update or a tampered copy:

```text
14903579ff54664cd72f8e8699e6961a6272c21863ec1c3b118cdc8af5d4a972  xterm/xterm.js
854a7c0fb70e8b1a083c16797ab827299fb18744f5ad34f227b48337e33293c6  xterm/xterm.css
ba3ea256ce0620a0992a197d6c9baea64823fc93d8da07a9e366ca9943c18527  addon-fit/addon-fit.js
```

The source maps are left out; the last line of each script still names
one, which only matters while developer tools are open (Debug builds).

To update: download the two packages from the npm registry, copy the same
files over these, update the versions and hashes above and in
[docs/ui.md](../../../../docs/ui.md), and check the options `terminal.js`
passes to `new Terminal(...)` against the new `typings/xterm.d.ts`.
