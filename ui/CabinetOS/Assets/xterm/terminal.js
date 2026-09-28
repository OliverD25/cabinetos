// The terminal page: one xterm.js terminal per session, fed by the window.
// The window owns the sessions and their byte pipes; this page only draws
// output and turns keys into input. Messages (docs/ui.md, "The terminal"):
//   window -> page: create, output (base64), show, close, exited, focus, passKeys, theme
//   page -> window: ready, input (text), binary (base64), resize, buffer, key
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
  let shown = 0;
  let passKeys = new Set();
  let look = {
    fontFamily: "'Cascadia Code', 'Cascadia Mono', Consolas, monospace",
    fontSize: 12,
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

  function onKey(term, event) {
    if (event.type !== 'keydown') {
      return true;
    }
    const keys = combo(event);
    if (keys && passKeys.has(keys)) {
      event.preventDefault();
      post({ type: 'key', keys });
      return false;
    }
    // Ctrl+C copies when text is selected, as in Windows Terminal; otherwise it goes to the shell.
    if (keys === 'ctrl+c' && term.hasSelection()) {
      navigator.clipboard.writeText(term.getSelection()).catch(() => {});
      term.clearSelection();
      event.preventDefault();
      return false;
    }
    // Ctrl+V: the browser's own paste event reaches xterm.js, which sends the text
    // (bracketed when the shell asked for it) instead of a ^V.
    if (keys === 'ctrl+v') {
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
      lineHeight: 1.25,
      theme: look.theme,
      allowTransparency: true,
      cursorBlink: true,
      scrollback: 5000,
      windowsPty: { backend: 'conpty', buildNumber: build },
    });
    const fit = new FitAddon.FitAddon();
    term.loadAddon(fit);
    term.attachCustomKeyEventHandler((event) => onKey(term, event));
    term.onData((data) => post({ type: 'input', session: id, data }));
    term.onBinary((data) => post({ type: 'binary', session: id, data: btoa(data) }));
    term.onResize(({ cols, rows }) => post({ type: 'resize', session: id, cols, rows }));
    term.buffer.onBufferChange((buffer) => post({ type: 'buffer', session: id, alternate: buffer.type === 'alternate' }));
    term.open(element);
    const session = { term, fit, element };
    sessions.set(id, session);
    reportSize(id, session);
  }

  function show(id) {
    shown = id;
    for (const [key, session] of sessions) {
      session.element.classList.toggle('shown', key === id);
    }
    const session = sessions.get(id);
    if (session) {
      reportSize(id, session);
    }
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

  function applyLook(message) {
    look = {
      fontFamily: message.fontFamily || look.fontFamily,
      fontSize: message.fontSize || look.fontSize,
      theme: {
        ...look.theme,
        background: message.background || look.theme.background,
        foreground: message.foreground || look.theme.foreground,
        cursor: message.cursor || look.theme.cursor,
        selectionBackground: message.selection || look.theme.selectionBackground,
      },
    };
    for (const [id, session] of sessions) {
      session.term.options.fontFamily = look.fontFamily;
      session.term.options.fontSize = look.fontSize;
      session.term.options.theme = look.theme;
      if (id === shown) {
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
        const current = sessions.get(shown);
        if (current) {
          current.term.focus();
        }
        break;
      }
      case 'passKeys':
        passKeys = new Set(message.keys || []);
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
      post({ type: 'key', keys });
    }
  });

  new ResizeObserver(() => {
    const session = sessions.get(shown);
    if (session) {
      reportSize(shown, session);
    }
  }).observe(container);

  post({ type: 'ready' });
})();
