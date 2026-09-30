// A test tool for the sidebar's page (docs/tool-extensions.md, "The sidebar page"): it says what the window told it.
(() => {
  const webview = window.chrome && window.chrome.webview;
  const folder = document.getElementById('folder');
  const count = document.getElementById('count');
  let told = 0;
  if (!webview) {
    return;
  }
  webview.addEventListener('message', (event) => {
    let message;
    try {
      message = JSON.parse(event.data);
    } catch {
      return;
    }
    if (message && message.type === 'context') {
      told += 1;
      folder.textContent = message.activeFolder || '(no folder)';
      count.textContent = String(told);
    }
  });
  webview.postMessage(JSON.stringify({ type: 'ready' }));
})();
