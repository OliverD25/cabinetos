// Agent Chat, a Tool Extension for the Agent plugin (docs/extensions/agent.md,
// docs/tool-extensions.md). The page sends the plugin's commands through the
// window and listens to the plugin's events:
//
//   page -> window   ready, subscribe {plugin}, command {id, args}
//   window -> page   context, plugin-event {plugin, name, payload}, paths-dropped {paths}
//
// The plugin tells the page what happened with events, so a command's
// result is not needed: agent.reply (an answer), agent.error, agent.notice,
// agent.preview (the window opens the preview itself), agent.tier and
// agent.audit. Everything from the model goes in with textContent.
'use strict';

(() => {
  const webview = window.chrome && window.chrome.webview;
  const post = (message) => webview && webview.postMessage(JSON.stringify(message));
  const $ = (id) => document.getElementById(id);

  const PLUGIN = 'agent';
  // The agent asks a model over the network: minutes are normal for a long
  // task, and the send button waits at most this long.
  const BUSY_LIMIT_MS = 180000;
  const CONFIRM_MS = 8000;
  const MAX_ATTACHED = 20;
  const MAX_LISTED = 50;

  const TIERS = {
    1: 'Advisor: it looks and advises. It changes nothing.',
    2: 'Diff and approve: it proposes changes as a preview, and you apply them.',
    3: 'Autonomous: it applies changes at once. Undo brings them back.',
  };

  const thread = $('thread');
  const audit = $('audit');
  const input = $('input');
  const send = $('send');
  const tierNote = $('tier-note');
  const tierButtons = [...document.querySelectorAll('.tiers button')];

  let tier = null;
  let busy = false;
  let busyTimer = 0;
  let confirmTimer = 0;
  let pending = null;
  let selection = [];
  let attached = [];

  function el(tag, className, text) {
    const node = document.createElement(tag);
    if (className) {
      node.className = className;
    }
    if (text !== undefined) {
      node.textContent = text;
    }
    return node;
  }

  const nameOf = (path) => {
    const cut = Math.max(path.lastIndexOf('\\'), path.lastIndexOf('/'));
    return cut >= 0 && cut < path.length - 1 ? path.slice(cut + 1) : path;
  };

  function scrollDown() {
    thread.scrollTop = thread.scrollHeight;
  }

  function add(node) {
    const empty = $('empty');
    if (empty) {
      empty.remove();
    }
    thread.insertBefore(node, pending);
    scrollDown();
  }

  function addLine(text, kind) {
    add(el('p', `line ${kind || ''}`.trim(), text));
  }

  function pathList(paths) {
    const list = el('ul', 'paths');
    for (const path of paths.slice(0, 5)) {
      const item = el('li', '', nameOf(path));
      item.title = path;
      list.appendChild(item);
    }
    if (paths.length > 5) {
      list.appendChild(el('li', '', `and ${paths.length - 5} more`));
    }
    return list;
  }

  function addUser(text, paths) {
    const turn = el('div', 'turn user');
    turn.appendChild(el('div', '', text));
    if (paths && paths.length > 0) {
      turn.appendChild(pathList(paths));
    }
    add(turn);
  }

  // A command line and what became of it: its status is one word, then why.
  function commandRow(command) {
    const status = String(command.status || '');
    const word = status.split(/[:\s]/)[0] || command.kind || '';
    const row = el('li');
    const tag = el('span', `tag ${word}`, word);
    tag.title = status;
    row.appendChild(tag);
    row.appendChild(el('code', '', command.line));
    if (status.length > word.length) {
      row.appendChild(el('span', 'hint', status.slice(word.length).replace(/^[:\s]+/, '')));
    }
    return row;
  }

  function addAgent(reply) {
    const source = reply.source;
    if (source === 'ask' && reply.prompt) {
      addUser(reply.prompt, []);
    }
    const turn = el('div', 'turn agent');
    if (source === 'rule') {
      turn.appendChild(el('p', 'from', 'A watch rule'));
      if (reply.prompt) {
        turn.appendChild(el('p', 'hint', reply.prompt));
      }
    }
    const words = el('div', 'words');
    for (const paragraph of String(reply.text || '').split(/\n{2,}/)) {
      if (paragraph.trim()) {
        words.appendChild(el('p', '', paragraph.trim()));
      }
    }
    turn.appendChild(words);

    const changes = Array.isArray(reply.changes) ? reply.changes : [];
    if (changes.length > 0) {
      const list = el('ul', 'changes');
      for (const change of changes.slice(0, MAX_LISTED)) {
        list.appendChild(el('li', '', change));
      }
      if (changes.length > MAX_LISTED) {
        list.appendChild(el('li', '', `and ${changes.length - MAX_LISTED} more`));
      }
      turn.appendChild(list);
    }

    const commands = Array.isArray(reply.commands) ? reply.commands : [];
    if (commands.length > 0) {
      const details = el('details');
      details.appendChild(el('summary', '', commands.length === 1 ? '1 command' : `${commands.length} commands`));
      const list = el('ul', 'lines');
      for (const command of commands) {
        list.appendChild(commandRow(command));
      }
      details.appendChild(list);
      // A command that did not work is what the person wants to see first.
      details.open = commands.some((command) => /^(error|blocked|invalid)/.test(String(command.status || '')));
      turn.appendChild(details);
    }
    add(turn);
  }

  // ----- busy, and the composer -----

  function setBusy(on) {
    busy = on;
    send.disabled = on;
    clearTimeout(busyTimer);
    if (pending) {
      pending.remove();
      pending = null;
    }
    if (on) {
      pending = el('p', 'pending', 'The agent is working...');
      thread.appendChild(pending);
      scrollDown();
      busyTimer = setTimeout(() => {
        setBusy(false);
        addLine('There is no answer yet. If the agent is still working, its answer shows when it comes.', 'notice');
      }, BUSY_LIMIT_MS);
    }
  }

  function renderAttached() {
    const box = $('attached');
    box.textContent = '';
    for (const path of attached) {
      const chip = el('span', 'chip');
      const label = el('span', '', nameOf(path));
      label.title = path;
      chip.appendChild(label);
      const remove = el('button', '', '×');
      remove.type = 'button';
      remove.title = 'Do not point at this file';
      remove.setAttribute('aria-label', `Remove ${nameOf(path)}`);
      remove.addEventListener('click', () => {
        attached = attached.filter((other) => other !== path);
        renderAttached();
      });
      chip.appendChild(remove);
      box.appendChild(chip);
    }
    box.hidden = attached.length === 0;
    renderSelection();
  }

  // The selection of the active pane goes along unless the person points at
  // other files, by dropping them here or by unticking the box.
  function renderSelection() {
    const show = selection.length > 0 && attached.length === 0;
    $('use-selection').hidden = !show;
    if (show) {
      $('use-selection-text').textContent = selection.length === 1
        ? `Point at the selected file (${nameOf(selection[0])})`
        : `Point at the ${selection.length} selected files`;
    }
  }

  function pointedAt() {
    if (attached.length > 0) {
      return attached;
    }
    return $('use-selection-box').checked ? selection : [];
  }

  function submit() {
    const text = input.value.trim();
    if (!text || busy) {
      return;
    }
    const paths = pointedAt();
    addUser(text, paths);
    input.value = '';
    attached = [];
    renderAttached();
    setBusy(true);
    post({ type: 'command', id: 'agent.chat', args: { message: text, paths } });
  }

  $('composer').addEventListener('submit', (event) => {
    event.preventDefault();
    submit();
  });

  input.addEventListener('keydown', (event) => {
    if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
      event.preventDefault();
      submit();
    }
  });

  // ----- the tier -----

  function note(text, warn) {
    tierNote.textContent = text;
    tierNote.classList.toggle('warn', Boolean(warn));
  }

  function showTier(number) {
    tier = number;
    clearTimeout(confirmTimer);
    for (const button of tierButtons) {
      button.setAttribute('aria-checked', String(Number(button.dataset.tier) === number));
      button.classList.remove('confirm');
    }
    note(TIERS[number] || '');
  }

  for (const button of tierButtons) {
    button.addEventListener('click', () => {
      const wanted = Number(button.dataset.tier);
      if (wanted === tier) {
        return;
      }
      // Changing files without asking is never a mistake made with one click.
      if (wanted === 3 && !button.classList.contains('confirm')) {
        button.classList.add('confirm');
        note('Autonomous applies changes at once. Click Autonomous again to allow it.', true);
        clearTimeout(confirmTimer);
        confirmTimer = setTimeout(() => {
          button.classList.remove('confirm');
          note(TIERS[tier] || '');
        }, CONFIRM_MS);
        return;
      }
      clearTimeout(confirmTimer);
      // An empty or given "input" keeps the window from asking for the tier in a prompt.
      post({ type: 'command', id: 'agent.tier', args: { tier: wanted, input: String(wanted) } });
    });
  }

  $('undo').addEventListener('click', () => {
    post({ type: 'command', id: 'agent.undo', args: {} });
  });

  // ----- the log -----

  function whenText(iso) {
    const time = new Date(iso);
    return Number.isNaN(time.getTime()) ? String(iso) : time.toLocaleString();
  }

  function entryNode(entry) {
    const node = el('div', 'entry');
    const head = el('div', 'head');
    head.appendChild(el('span', '', whenText(entry.time)));
    head.appendChild(el('span', '', entry.source || ''));
    head.appendChild(el('span', '', `tier ${entry.tier}`));
    head.appendChild(el('span', '', `${entry.provider || ''} ${entry.model || ''}`.trim()));
    node.appendChild(head);
    node.appendChild(el('div', 'prompt', entry.prompt || ''));
    if (entry.error) {
      node.appendChild(el('div', 'failed', entry.error));
    }
    if (entry.preview) {
      node.appendChild(el('div', 'hint', `Preview ${entry.preview}`));
    }
    if (Array.isArray(entry.jobs) && entry.jobs.length > 0) {
      node.appendChild(el('div', 'hint', `Jobs ${entry.jobs.join(', ')}`));
    }
    const commands = (Array.isArray(entry.rounds) ? entry.rounds : []).flatMap((round) => round.commands || []);
    if (commands.length > 0) {
      const details = el('details');
      details.appendChild(el('summary', '', commands.length === 1 ? '1 command' : `${commands.length} commands`));
      const list = el('ul', 'lines');
      for (const command of commands) {
        list.appendChild(commandRow({ line: command.line, kind: command.kind, status: command.outcome }));
      }
      details.appendChild(list);
      node.appendChild(details);
    }
    return node;
  }

  function renderAudit(entries) {
    audit.textContent = '';
    const list = Array.isArray(entries) ? entries : [];
    if (list.length === 0) {
      audit.appendChild(el('p', 'empty', 'The agent has not been asked anything yet.'));
      return;
    }
    // Newest first: it is the one people look for.
    for (const entry of [...list].reverse()) {
      audit.appendChild(entryNode(entry));
    }
  }

  $('log').addEventListener('click', () => {
    const showLog = audit.hidden;
    audit.hidden = !showLog;
    thread.hidden = showLog;
    $('log').setAttribute('aria-pressed', String(showLog));
    if (showLog) {
      post({ type: 'command', id: 'agent.audit', args: { n: 30 } });
    }
  });

  // ----- what the window says -----

  function payloadOf(message) {
    let payload = message.payload;
    if (typeof payload === 'string') {
      try {
        payload = JSON.parse(payload);
      } catch {
        return { text: payload, notice: payload };
      }
    }
    return payload && typeof payload === 'object' ? payload : {};
  }

  function onPluginEvent(message) {
    const payload = payloadOf(message);
    switch (message.name) {
      case 'agent.reply':
        setBusy(false);
        addAgent(payload);
        break;
      case 'agent.error':
        setBusy(false);
        addLine(payload.text || 'The agent could not do that.', 'error');
        break;
      case 'agent.notice':
        if (payload.notice) {
          addLine(payload.notice, 'notice');
        }
        break;
      case 'agent.preview':
        // The window opens the preview in the other pane by itself.
        addLine('The changes are shown as a preview in the other pane. Apply them there, or cancel.', 'notice');
        break;
      case 'agent.tier':
        showTier(Number(payload.tier));
        break;
      case 'agent.audit':
        renderAudit(payload.entries);
        break;
      default:
        break;
    }
  }

  function onPathsDropped(message) {
    if (!Array.isArray(message.paths)) {
      return;
    }
    for (const path of message.paths) {
      if (typeof path === 'string' && !attached.includes(path) && attached.length < MAX_ATTACHED) {
        attached.push(path);
      }
    }
    renderAttached();
    input.focus();
  }

  function onMessage(message) {
    switch (message && message.type) {
      case 'context':
        selection = Array.isArray(message.selection) ? message.selection.filter((path) => typeof path === 'string') : [];
        renderSelection();
        break;
      case 'plugin-event':
        if (message.plugin === PLUGIN) {
          onPluginEvent(message);
        }
        break;
      case 'paths-dropped':
        onPathsDropped(message);
        break;
      default:
        break;
    }
  }

  if (webview) {
    webview.addEventListener('message', (event) => {
      let message;
      try {
        message = JSON.parse(event.data);
      } catch {
        return;
      }
      onMessage(message);
    });
    post({ type: 'ready' });
    // A page that loads again has forgotten what it followed.
    post({ type: 'subscribe', plugin: PLUGIN });
    // Which tier is set: the plugin answers with agent.tier.
    post({ type: 'command', id: 'agent.tier', args: { input: '' } });
  } else {
    note('This page is meant to run inside CabinetOS.', true);
    send.disabled = true;
  }
})();
