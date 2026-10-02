// The terminal page: one xterm.js terminal per session, fed by the window.
// The window owns the sessions and their byte pipes; this page only draws
// output and turns keys into input. Messages (docs/ui.md, "The terminal"):
//   window -> page: create, output (base64), show, view, close, exited, focus, passKeys, theme, paste
//   page -> window: ready, input (text), binary (base64), resize, buffer, key, paste, focused
// `show` puts one session across the whole page. `view` is the split mirror: the sessions on screen, each
// at its place across the page (x and width in pixels), and a hint where a pane has no session.
'use strict';

(() => {
  const webview = window.chrome && window.chrome.webview;
  if (!webview) {
    return;
  }

  // The Windows build, for xterm.js's ConPTY heuristics (reflow, scrollback on resize).
  const build = Number(new URLSearchParams(location.search).get('build')) || 0;
  const container = document.getElementById('terminals');
  const sessions = new Map();
  // The places on screen: [{ session, x, width } or { hint, x, width }]; x and width 0 is the whole page.
  let places = [];
  let hints = [];
  let passKeys = new Set();
  let look = {
    fontFamily: "'Cascadia Code', 'Cascadia Mono', Consolas, monospace",
    fontSize: 12,
    lineHeight: 1.25,
    theme: {
      background: '#00000000',
      foreground: '#FFFFFFE6',
      cursor: '#60CDFF',
      cursorAccent: '#000000',
      selectionBackground: '#60CDFF4D',
      scrollbarSliderBackground: '#FFFFFF1F',
      scrollbarSliderHoverBackground: '#FFFFFF33',
      scrollbarSliderActiveBackground: '#FFFFFF4D',
    },
  };

  const post = (message) => webview.postMessage(JSON.stringify(message));

  // The window's key names (KeyNames.cs) come from Windows virtual-key codes,
  // which Chromium on Windows reports as keyCode; the same table here makes
  // the same names, so "ctrl+backquote" means one thing on both sides.
  const NAMED = {
    8: 'backspace', 9: 'tab', 13: 'enter', 27: 'escape', 32: 'space', 33: 'pageup', 34: 'pagedown',
    35: 'end', 36: 'home', 37: 'left', 38: 'up', 39: 'right', 40: 'down', 45: 'insert', 46: 'delete',
    186: 'semicolon', 187: 'equal', 188: 'comma', 189: 'minus', 190: 'period', 191: 'slash',
    192: 'backquote', 219: 'bracketleft', 220: 'backslash', 221: 'bracketright', 222: 'quote',
  };

  function keyName(code) {
    if (NAMED[code]) {
      return NAMED[code];
    }
    if (code >= 48 && code <= 57) {
      return String(code - 48);
    }
    if (code >= 96 && code <= 105) {
      return String(code - 96);
    }
    if (code >= 65 && code <= 90) {
      return String.fromCharCode(code + 32);
    }
    if (code >= 112 && code <= 135) {
      return 'f' + (code - 111);
    }
    return null;
  }

  function combo(event) {
    const name = keyName(event.keyCode);
    if (!name) {
      return null;
    }
    return (event.ctrlKey ? 'ctrl+' : '') + (event.shiftKey ? 'shift+' : '') +
      (event.altKey ? 'alt+' : '') + (event.metaKey ? 'win+' : '') + name;
  }

  function decode(base64) {
    const text = atob(base64);
    const bytes = new Uint8Array(text.length);
    for (let i = 0; i < text.length; i++) {
      bytes[i] = text.charCodeAt(i);
    }
    return bytes;
  }

  // VK_PACKET: a character sent as a Unicode key event (the touch keyboard, Voice Access,
  // automation tools). Its keydown can carry the character of an earlier packet, which
  // xterm.js would type (the live check of 2026-09-28 got "ttttttttttttttt"); the keypress
  // that follows has the right one.
  const VK_PACKET = 231;

  function onKey(term, id, event) {
    if (event.type !== 'keydown') {
      return true;
    }
    if (event.keyCode === VK_PACKET) {
      return false;
    }
    const keys = combo(event);
    if (keys && passKeys.has(keys)) {
      event.preventDefault();
      // The keys passed on are the window's toggles and ways out: a key held down runs its command once.
      if (!event.repeat) {
        post({ type: 'key', keys });
      }
      return false;
    }
    // Ctrl+C copies when text is selected, as in Windows Terminal; otherwise it goes to the shell.
    // Ctrl+Shift+C always copies (nothing without a selection) and never reaches the shell.
    if ((keys === 'ctrl+c' && term.hasSelection()) || keys === 'ctrl+shift+c') {
      if (term.hasSelection()) {
        navigator.clipboard.writeText(term.getSelection()).catch(() => {});
        term.clearSelection();
      }
      event.preventDefault();
      return false;
    }
    // Ctrl+V: the browser's own paste event reaches xterm.js, which sends the text (bracketed when
    // the shell asked for it) instead of a ^V.
    if (keys === 'ctrl+v') {
      return false;
    }
    // Ctrl+Shift+V is a browser key (paste as plain text), and WebView2's browser keys are off; the
    // page may not read the clipboard either. So the window reads it and sends the text back (paste).
    if (keys === 'ctrl+shift+v') {
      event.preventDefault();
      if (!event.repeat) {
        post({ type: 'paste', session: id });
      }
      return false;
    }
    return true;
  }

  function reportSize(id, session) {
    try {
      session.fit.fit();
    } catch {
      // Not laid out yet; the next resize fits it.
    }
    post({ type: 'resize', session: id, cols: session.term.cols, rows: session.term.rows });
  }

  function create(id) {
    if (sessions.has(id)) {
      return;
    }
    const element = document.createElement('div');
    element.className = 'session';
    container.appendChild(element);
    const term = new Terminal({
      fontFamily: look.fontFamily,
      fontSize: look.fontSize,
      lineHeight: look.lineHeight,
      theme: look.theme,
      allowTransparency: true,
      cursorBlink: true,
      scrollback: 5000,
      windowsPty: { backend: 'conpty', buildNumber: build },
    });
    const fit = new FitAddon.FitAddon();
    term.loadAddon(fit);
    term.attachCustomKeyEventHandler((event) => onKey(term, id, event));
    // Whichever way a terminal got the keyboard (a click into its text, the window's focus message), the
    // window learns which one has it: in the split mirror that says which half the keys go to.
    element.addEventListener('focusin', () => post({ type: 'focused', session: id }));
    term.onData((data) => post({ type: 'input', session: id, data }));
    term.onBinary((data) => post({ type: 'binary', session: id, data: btoa(data) }));
    term.onResize(({ cols, rows }) => post({ type: 'resize', session: id, cols, rows }));
    term.buffer.onBufferChange((buffer) => post({ type: 'buffer', session: id, alternate: buffer.type === 'alternate' }));
    term.open(element);
    const session = { term, fit, element };
    sessions.set(id, session);
    reportSize(id, session);
  }

  // Puts the sessions on screen at their places across the page, and a hint in a place that has none. The
  // places are in pixels; a place with no width is the whole page (the one view).
  function view(list) {
    places = list;
    for (const hint of hints) {
      hint.remove();
    }
    hints = [];
    const width = container.clientWidth;
    const onScreen = new Set();
    for (const place of list) {
      const x = Number(place.x) || 0;
      const span = Number(place.width) || 0;
      const right = span > 0 ? Math.max(0, width - x - span) : 0;
      if (place.session && sessions.has(place.session)) {
        const style = sessions.get(place.session).element.style;
        style.setProperty('--x', `${x}px`);
        style.setProperty('--right', `${right}px`);
        onScreen.add(place.session);
      } else if (typeof place.hint === 'string') {
        const hint = document.createElement('div');
        hint.className = 'hint';
        hint.textContent = place.hint;
        hint.style.setProperty('--x', `${x}px`);
        hint.style.setProperty('--right', `${right}px`);
        container.appendChild(hint);
        hints.push(hint);
      }
    }
    for (const [id, session] of sessions) {
      session.element.classList.toggle('shown', onScreen.has(id));
    }
    for (const id of onScreen) {
      reportSize(id, sessions.get(id));
    }
  }

  function show(id) {
    view([{ session: id }]);
  }

  function close(id) {
    const session = sessions.get(id);
    if (!session) {
      return;
    }
    session.term.dispose();
    session.element.remove();
    sessions.delete(id);
  }

  // The theme's 16 ANSI colours, in the order docs/themes.md gives them.
  const ANSI_KEYS = ['black', 'red', 'green', 'yellow', 'blue', 'magenta', 'cyan', 'white',
    'brightBlack', 'brightRed', 'brightGreen', 'brightYellow', 'brightBlue', 'brightMagenta', 'brightCyan', 'brightWhite'];

  function applyLook(message) {
    const ansi = {};
    if (Array.isArray(message.ansi) && message.ansi.length === 16) {
      ANSI_KEYS.forEach((key, i) => { ansi[key] = message.ansi[i]; });
    }
    look = {
      fontFamily: message.fontFamily || look.fontFamily,
      fontSize: message.fontSize || look.fontSize,
      lineHeight: message.lineHeight || look.lineHeight,
      theme: {
        ...look.theme,
        ...ansi,
        background: message.background || look.theme.background,
        foreground: message.foreground || look.theme.foreground,
        cursor: message.cursor || look.theme.cursor,
        selectionBackground: message.selection || look.theme.selectionBackground,
      },
    };
    document.documentElement.style.setProperty('--hint-color', look.theme.foreground);
    // The theme's space around the text (top, right, bottom, left); terminal.css has the default look's.
    if (Array.isArray(message.padding) && message.padding.length === 4) {
      ['top', 'right', 'bottom', 'left'].forEach((side, i) => {
        document.documentElement.style.setProperty(`--pad-${side}`, `${Number(message.padding[i]) || 0}px`);
      });
    }
    for (const [id, session] of sessions) {
      session.term.options.fontFamily = look.fontFamily;
      session.term.options.fontSize = look.fontSize;
      session.term.options.lineHeight = look.lineHeight;
      session.term.options.theme = look.theme;
      if (places.some((place) => place.session === id)) {
        reportSize(id, session);
      }
    }
  }

  webview.addEventListener('message', (event) => {
    let message;
    try {
      message = JSON.parse(event.data);
    } catch {
      return;
    }
    const session = sessions.get(message.session);
    switch (message.type) {
      case 'create':
        create(message.session);
        break;
      case 'output':
        if (session) {
          session.term.write(decode(message.data));
        }
        break;
      case 'show':
        show(message.session);
        break;
      case 'view':
        view(Array.isArray(message.places) ? message.places : []);
        break;
      case 'close':
        close(message.session);
        break;
      case 'exited':
        if (session) {
          session.term.write('\r\n\x1b[2m[exited with code ' + message.code + ']\x1b[0m');
          session.term.options.disableStdin = true;
          session.term.options.cursorBlink = false;
        }
        break;
      case 'focus': {
        // The session the window names: in the split mirror the half that has the keyboard. Without one,
        // the first session on screen.
        const target = session || sessions.get((places.find((place) => place.session) || {}).session);
        if (target) {
          target.term.focus();
        }
        break;
      }
      case 'passKeys':
        passKeys = new Set(message.keys || []);
        break;
      case 'paste':
        if (session && typeof message.text === 'string') {
          session.term.paste(message.text);
        }
        break;
      case 'theme':
        applyLook(message);
        break;
    }
  });

  // A window shortcut pressed while no terminal has the keyboard (the page itself has focus).
  document.addEventListener('keydown', (event) => {
    if (event.defaultPrevented) {
      return;
    }
    const keys = combo(event);
    if (keys && passKeys.has(keys)) {
      event.preventDefault();
      if (!event.repeat) {
        post({ type: 'key', keys });
      }
    }
  });

  // The page changed size: the places keep their pixels from the left, so the distance to the right edge
  // is worked out again, and every terminal on screen fits its new place.
  new ResizeObserver(() => view(places)).observe(container);

  post({ type: 'ready' });
})();
