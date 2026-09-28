// Markdown Preview, the first Tool Extension (docs/tool-extensions.md).
// The window sends {"type":"open","path":…,"url":…}; the page reads the file
// from that URL (the file's folder, served read-only), renders it with marked,
// and asks the window to open a linked Markdown file with a command.
'use strict';

(() => {
  const webview = window.chrome && window.chrome.webview;
  const content = document.getElementById('content');
  const base = document.getElementById('base');
  let current = null;

  const post = (message) => webview && webview.postMessage(JSON.stringify(message));

  // GitHub's heading anchors: lower case, spaces to "-", punctuation dropped.
  function slug(text, used) {
    let id = text.trim().toLowerCase().replace(/[^\p{L}\p{N}\s-]/gu, '').replace(/\s/g, '-');
    const plain = id;
    for (let n = 1; used.has(id); n++) {
      id = `${plain}-${n}`;
    }
    used.add(id);
    return id;
  }

  function showError(text) {
    content.textContent = '';
    const paragraph = document.createElement('p');
    paragraph.className = 'error';
    paragraph.textContent = text;
    content.appendChild(paragraph);
  }

  async function open(path, url) {
    const cut = path.lastIndexOf('\\');
    current = { path, folder: cut > 0 ? path.slice(0, cut) : path };
    document.title = path.slice(cut + 1);
    let text;
    try {
      const response = await fetch(url, { cache: 'no-store' });
      if (!response.ok) {
        throw new Error(`${response.status} ${response.statusText}`);
      }
      text = await response.text();
    } catch (error) {
      showError(`This file could not be read: ${error.message}`);
      return;
    }
    // Relative links and images resolve against the file's own folder.
    base.href = url.slice(0, url.lastIndexOf('/') + 1);
    content.innerHTML = marked.parse(text, { gfm: true });
    const used = new Set();
    for (const heading of content.querySelectorAll('h1, h2, h3, h4, h5, h6')) {
      heading.id = slug(heading.textContent || '', used);
    }
    for (const link of content.querySelectorAll('a[href]')) {
      if (/^[a-z][a-z0-9+.-]*:/i.test(link.getAttribute('href'))) {
        link.classList.add('web');
        link.title = 'Tools have no network: web links do not open here';
      }
    }
    window.scrollTo(0, 0);
  }

  // "..\docs\x.md" from the file's folder, as a Windows path.
  function resolve(folder, relative) {
    const parts = folder.split('\\');
    for (const segment of relative.replace(/\//g, '\\').split('\\')) {
      if (segment === '' || segment === '.') {
        continue;
      }
      if (segment === '..') {
        if (parts.length > 1) {
          parts.pop();
        }
      } else {
        parts.push(segment);
      }
    }
    return parts.join('\\');
  }

  document.addEventListener('click', (event) => {
    const link = event.target.closest && event.target.closest('a[href]');
    if (!link) {
      return;
    }
    // The page never navigates: the window would refuse it anyway.
    event.preventDefault();
    const href = link.getAttribute('href');
    if (href.startsWith('#')) {
      const target = document.getElementById(decodeURIComponent(href.slice(1)));
      if (target) {
        target.scrollIntoView();
      }
      return;
    }
    if (/^[a-z][a-z0-9+.-]*:/i.test(href) || !current) {
      return;
    }
    const file = decodeURIComponent(href.split('#')[0]);
    if (/\.(md|markdown)$/i.test(file)) {
      post({ type: 'command', id: 'editor.openMarkdownPreview', args: { path: resolve(current.folder, file) } });
    }
  });

  if (webview) {
    webview.addEventListener('message', (event) => {
      let message;
      try {
        message = JSON.parse(event.data);
      } catch {
        return;
      }
      if (message && message.type === 'open' && typeof message.path === 'string' && typeof message.url === 'string') {
        open(message.path, message.url);
      }
    });
    post({ type: 'ready' });
  }
})();
