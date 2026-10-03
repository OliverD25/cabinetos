// The Quick View test viewer (ADR 0023, "The fixture viewer"). A .qvtest
// file's first line says what the page does:
//   shown                    reports quickview-shown
//   shown-keys left right    reports quickview-shown asking for those keys
//   failed                   reports quickview-failed
//   hang                     posts ready and nothing more
//   crash                    runs an endless loop
//   slow 4000                reports quickview-shown after 4000 ms
// A PNG is shown as an image, and reported once it is painted.
"use strict";

const content = document.getElementById("content");

function post(message) {
  window.chrome.webview.postMessage(JSON.stringify(message));
}

function frames(count) {
  return new Promise((resolve) => {
    const step = (left) => (left === 0 ? resolve() : requestAnimationFrame(() => step(left - 1)));
    step(count);
  });
}

async function shown(token, keys, details) {
  await frames(2);
  const message = { type: "quickview-shown", token };
  if (keys.length > 0) {
    message.keys = keys;
  }
  if (details) {
    message.details = details;
  }
  post(message);
}

async function showImage(show) {
  const image = new Image();
  image.src = show.url;
  content.replaceChildren(image);
  try {
    await image.decode();
  } catch {
    post({ type: "quickview-failed", token: show.token, reason: "damaged", message: "The image cannot be decoded." });
    return;
  }
  await shown(show.token, [], `${image.naturalWidth} × ${image.naturalHeight}`);
}

async function showTest(show) {
  const text = await (await fetch(show.url)).text();
  const words = (text.split(/\r?\n/)[0] || "").trim().split(/\s+/);
  const [what, ...rest] = words;
  content.textContent = words.join(" ");
  switch (what) {
    case "shown":
      await shown(show.token, [], "fixture");
      break;
    case "shown-keys":
      await shown(show.token, rest, "fixture");
      break;
    case "failed":
      post({ type: "quickview-failed", token: show.token, reason: "unsupported", message: "The fixture was told to fail." });
      break;
    case "hang":
      break;
    case "crash":
      for (;;) {
        // An endless loop: the page stops answering.
      }
    case "slow":
      setTimeout(() => shown(show.token, [], "fixture"), Number(rest[0]) || 0);
      break;
    default:
      post({ type: "quickview-failed", token: show.token, reason: "other", message: `Unknown fixture line: ${what}` });
  }
}

window.chrome.webview.addEventListener("message", (event) => {
  let message;
  try {
    message = typeof event.data === "string" ? JSON.parse(event.data) : event.data;
  } catch {
    return;
  }
  if (message.type === "quickview-show") {
    const run = message.extension === ".png" ? showImage : showTest;
    run(message).catch((error) => {
      post({ type: "quickview-failed", token: message.token, reason: "other", message: String(error).slice(0, 200) });
    });
  } else if (message.type === "quickview-key") {
    content.dataset.lastKey = message.key;
  }
});

post({ type: "ready" });
