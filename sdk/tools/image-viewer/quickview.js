// The Image Viewer's Quick View page (ADR 0023). One file, one load: the page
// posts ready, gets one quickview-show, and reports quickview-shown after the
// picture is painted or quickview-failed. Formats the browser decodes are read
// from the file's url at full resolution; the others are drawn by Windows'
// image stack (quickview-render) and shown from the render's url.
"use strict";

const MAX_BYTES = 256 * 1024 * 1024;
const MAX_RENDER = 2560;
const MAX_PERCENT = 800;
const STEP_PERCENT = 25;
const PIXELATED_FROM_PERCENT = 300;
const RENDER_EXTENSIONS = new Set([".heic", ".heif", ".tif", ".tiff", ".jxr", ".dng", ".cr2", ".nef", ".arw"]);
// The window grants only the keys no binding wants. Shift+plus is the plus sign on a US keyboard.
const KEYS = ["plus", "shift+plus", "minus", "0", "1"];

const stage = document.getElementById("stage");
const badge = document.getElementById("badge");

let show = null;
let load = 0;
let picture = null;
let natural = { width: 0, height: 0 };
// CSS pixels per image pixel at 100 %: one image pixel per device pixel (an SVG has no pixels: one per CSS pixel).
let unit = 1;
let view = { fit: true, scale: 1, x: 0, y: 0 };
let badgeTimer = 0;

function post(message) {
  window.chrome.webview.postMessage(JSON.stringify(message));
}

function frames(count) {
  return new Promise((resolve) => {
    const step = (left) => (left === 0 ? resolve() : requestAnimationFrame(() => step(left - 1)));
    step(count);
  });
}

function fail(reason, message) {
  post({ type: "quickview-failed", token: show.token, reason, message: message.slice(0, 200) });
}

function applyTheme(theme) {
  const root = document.documentElement;
  root.style.setProperty("--background", theme.background);
  root.style.setProperty("--text", theme.text);
  root.style.setProperty("--text-secondary", theme.textSecondary);
  root.style.setProperty("--accent", theme.accent);
  root.style.colorScheme = theme.appearance;
  document.body.style.fontFamily = `"${theme.font}", "Segoe UI Variable", "Segoe UI", sans-serif`;
}

function fitScale() {
  const width = Math.max(1, stage.clientWidth);
  const height = Math.max(1, stage.clientHeight);
  return Math.min(unit, width / natural.width, height / natural.height);
}

function percent(scale) {
  return (scale / unit) * 100;
}


function clampPan() {
  const width = natural.width * view.scale;
  const height = natural.height * view.scale;
  const limitX = Math.max(0, (width - stage.clientWidth) / 2);
  const limitY = Math.max(0, (height - stage.clientHeight) / 2);
  view.x = Math.min(limitX, Math.max(-limitX, view.x));
  view.y = Math.min(limitY, Math.max(-limitY, view.y));
}

function layout() {
  if (!picture) {
    return;
  }
  if (view.fit) {
    view.scale = fitScale();
    view.x = 0;
    view.y = 0;
  }
  clampPan();
  picture.style.width = `${natural.width * view.scale}px`;
  picture.style.height = `${natural.height * view.scale}px`;
  picture.style.transform = `translate(-50%, -50%) translate(${view.x}px, ${view.y}px)`;
  picture.classList.toggle("pixelated", percent(view.scale) >= PIXELATED_FROM_PERCENT);
  stage.classList.toggle("pannable", natural.width * view.scale > stage.clientWidth + 0.5 || natural.height * view.scale > stage.clientHeight + 0.5);
}

function flash(text) {
  badge.textContent = text;
  badge.classList.add("on");
  clearTimeout(badgeTimer);
  badgeTimer = setTimeout(() => badge.classList.remove("on"), 1200);
}

// Zooms to `scale`, keeping the image point under (anchorX, anchorY), which are
// measured from the stage's centre, where it is.
function zoomTo(scale, anchorX, anchorY) {
  // Never smaller than the fit: a picture smaller than the panel has nothing to gain from it.
  const next = Math.min((MAX_PERCENT * unit) / 100, Math.max(fitScale(), scale));
  const ratio = next / view.scale;
  view.x = anchorX - (anchorX - view.x) * ratio;
  view.y = anchorY - (anchorY - view.y) * ratio;
  view.scale = next;
  view.fit = next <= fitScale() + 1e-9;
  layout();
  flash(`${Math.round(percent(view.scale))} %`);
}

function zoomStep(direction) {
  const current = percent(view.scale);
  const target = direction > 0
    ? (Math.floor(current / STEP_PERCENT + 1e-6) + 1) * STEP_PERCENT
    : (Math.ceil(current / STEP_PERCENT - 1e-6) - 1) * STEP_PERCENT;
  zoomTo((target * unit) / 100, 0, 0);
}

function zoomFit() {
  view = { fit: true, scale: fitScale(), x: 0, y: 0 };
  layout();
  flash("Fit");
}

function zoomActual() {
  zoomTo(unit, 0, 0);
}

async function showPicture(url, describe) {
  const mine = ++load;
  stage.querySelector("#picture")?.remove();
  const image = new Image();
  image.id = "picture";
  image.alt = "";
  image.draggable = false;
  image.src = url;
  stage.prepend(image);
  try {
    await image.decode();
  } catch {
    if (mine === load) {
      image.remove();
      fail("damaged", "The image cannot be decoded. The file may be damaged.");
    }
    return;
  }
  if (mine !== load) {
    return;
  }
  picture = image;
  natural = { width: image.naturalWidth, height: image.naturalHeight };
  if (natural.width === 0 || natural.height === 0) {
    // An SVG with a viewBox and no size: a box as large as the page's.
    natural = { width: Math.max(1, stage.clientWidth), height: Math.max(1, stage.clientHeight) };
  }
  unit = show.extension === ".svg" ? 1 : 1 / window.devicePixelRatio;
  view = { fit: true, scale: 1, x: 0, y: 0 };
  layout();
  await frames(2);
  if (mine !== load) {
    return;
  }
  post({ type: "quickview-shown", token: show.token, keys: KEYS, details: describe(image) });
}

function requestRender() {
  const scale = show.panel.scale;
  let width = show.panel.width * scale;
  let height = show.panel.height * scale;
  const longer = Math.max(width, height);
  if (longer > MAX_RENDER) {
    width = (width * MAX_RENDER) / longer;
    height = (height * MAX_RENDER) / longer;
  }
  post({
    type: "quickview-render",
    token: show.token,
    width: Math.max(1, Math.round(width)),
    height: Math.max(1, Math.round(height)),
  });
}

function start(message) {
  show = message;
  applyTheme(message.theme);
  if (message.size > MAX_BYTES) {
    const megabytes = Math.round(message.size / (1024 * 1024));
    fail("too-large", `This file is ${megabytes} MB. The Image Viewer shows files up to 256 MB.`);
    return;
  }
  if (RENDER_EXTENSIONS.has(message.extension)) {
    requestRender();
    return;
  }
  showPicture(message.url, (image) => `${image.naturalWidth} × ${image.naturalHeight}`);
}

window.chrome.webview.addEventListener("message", (event) => {
  let message;
  try {
    message = typeof event.data === "string" ? JSON.parse(event.data) : event.data;
  } catch {
    return;
  }
  switch (message.type) {
    case "quickview-show":
      start(message);
      break;
    case "quickview-theme":
      applyTheme(message.theme);
      break;
    case "quickview-rendered":
      showPicture(message.url, () => `Drawn by Windows · ${message.width} × ${message.height}`);
      break;
    case "quickview-render-failed":
      fail("unsupported", `Windows has no codec for this file. ${message.message ?? ""}`.trim());
      break;
    case "quickview-key":
      if (picture) {
        if (message.key === "plus" || message.key === "shift+plus") {
          zoomStep(1);
        } else if (message.key === "minus") {
          zoomStep(-1);
        } else if (message.key === "0") {
          zoomFit();
        } else if (message.key === "1") {
          zoomActual();
        }
      }
      break;
    default:
      break;
  }
});

stage.addEventListener(
  "wheel",
  (event) => {
    if (!picture) {
      return;
    }
    event.preventDefault();
    const box = stage.getBoundingClientRect();
    const anchorX = event.clientX - box.left - box.width / 2;
    const anchorY = event.clientY - box.top - box.height / 2;
    const unitsPerLine = event.deltaMode === 1 ? 33 : event.deltaMode === 2 ? 400 : 1;
    zoomTo(view.scale * Math.exp((-event.deltaY * unitsPerLine) * 0.0015), anchorX, anchorY);
  },
  { passive: false },
);

let drag = null;

stage.addEventListener("pointerdown", (event) => {
  if (!picture || event.button !== 0 || !stage.classList.contains("pannable")) {
    return;
  }
  drag = { id: event.pointerId, x: event.clientX, y: event.clientY };
  stage.setPointerCapture(event.pointerId);
  stage.classList.add("panning");
});

stage.addEventListener("pointermove", (event) => {
  if (!drag || event.pointerId !== drag.id) {
    return;
  }
  view.x += event.clientX - drag.x;
  view.y += event.clientY - drag.y;
  drag.x = event.clientX;
  drag.y = event.clientY;
  view.fit = false;
  layout();
});

function endDrag(event) {
  if (drag && event.pointerId === drag.id) {
    drag = null;
    stage.classList.remove("panning");
  }
}

stage.addEventListener("pointerup", endDrag);
stage.addEventListener("pointercancel", endDrag);

window.addEventListener("resize", layout);

post({ type: "ready" });
