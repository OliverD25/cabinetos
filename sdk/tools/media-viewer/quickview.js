// The Media Viewer's Quick View page (ADR 0023). One file, one load: the page
// posts ready, gets one quickview-show, plays the file from its url at once,
// and reports quickview-shown at the first picture (video) or the first sound
// (audio), or quickview-failed. The window never gives the page the keyboard:
// the keys below are asked for in quickview-shown and arrive as quickview-key.
// The window sends the page to about:blank when the panel closes, which stops
// playback; the page never loops.
"use strict";

const AUDIO_EXTENSIONS = new Set([".mp3", ".m4a", ".aac", ".flac", ".wav", ".ogg", ".opus", ".weba"]);
// Space is the panel's and closes it, so it is never asked for. Shift+plus is the plus sign on a US keyboard.
const KEYS = ["k", "j", "l", "comma", "period", "m", "plus", "shift+plus", "minus", "0", "1", "2", "3", "4", "5", "6", "7", "8", "9"];
// If the browser never presents a frame (a paused first frame, say), the panel must not wait for the 30 s limit.
const SHOWN_AFTER_LOADED_MS = 2000;

const stage = document.getElementById("stage");
const video = document.getElementById("video");
const sound = document.getElementById("sound");
const nameLabel = document.getElementById("name");
const player = document.getElementById("player");
const badge = document.getElementById("badge");

let show = null;
let media = null;
let reported = false;
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

function applyTheme(theme) {
  const root = document.documentElement;
  root.style.setProperty("--background", theme.background);
  root.style.setProperty("--text", theme.text);
  root.style.setProperty("--text-secondary", theme.textSecondary);
  root.style.setProperty("--accent", theme.accent);
  root.style.colorScheme = theme.appearance;
  document.body.style.fontFamily = `"${theme.font}", "Segoe UI Variable", "Segoe UI", sans-serif`;
}

function clock(seconds) {
  const total = Math.round(seconds);
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const rest = String(total % 60).padStart(2, "0");
  return hours > 0 ? `${hours}:${String(minutes).padStart(2, "0")}:${rest}` : `${minutes}:${rest}`;
}

function flash(text) {
  badge.textContent = text;
  badge.classList.add("on");
  clearTimeout(badgeTimer);
  badgeTimer = setTimeout(() => badge.classList.remove("on"), 1200);
}

function reportShown(details) {
  if (reported) {
    return;
  }
  reported = true;
  post({ type: "quickview-shown", token: show.token, keys: KEYS, details });
}

function reportFailed(error) {
  if (reported) {
    return;
  }
  reported = true;
  // MediaError.code: 1 aborted, 2 network, 3 decode, 4 source not supported.
  if (error && error.code === 4) {
    post({
      type: "quickview-failed",
      token: show.token,
      reason: "unsupported",
      message: "The browser engine cannot play this file: its container or codec is not supported on this PC.",
    });
  } else {
    post({
      type: "quickview-failed",
      token: show.token,
      reason: "damaged",
      message: "The file could not be read or decoded. It may be damaged.",
    });
  }
}

function startVideo() {
  media = video;
  video.hidden = false;
  const details = () => {
    const size = `${video.videoWidth} × ${video.videoHeight}`;
    return Number.isFinite(video.duration) ? `${clock(video.duration)} · ${size}` : size;
  };
  video.addEventListener("error", () => reportFailed(video.error), { once: true });
  video.addEventListener("loadedmetadata", () => {
    if (video.videoWidth === 0) {
      // A video container with sound only: show it as audio.
      video.removeAttribute("src");
      video.load();
      video.hidden = true;
      startAudio();
    }
  });
  video.addEventListener("loadeddata", () => {
    if (video.videoWidth === 0) {
      return;
    }
    if (!("requestVideoFrameCallback" in video)) {
      frames(2).then(() => reportShown(details()));
    }
    setTimeout(() => reportShown(details()), SHOWN_AFTER_LOADED_MS);
  });
  if ("requestVideoFrameCallback" in video) {
    video.requestVideoFrameCallback(() => {
      if (video.videoWidth > 0) {
        reportShown(details());
      }
    });
  }
  video.src = show.url;
  video.play().catch(() => {});
}

function startAudio() {
  media = player;
  sound.hidden = false;
  nameLabel.textContent = show.name;
  const details = () => (Number.isFinite(player.duration) ? clock(player.duration) : "");
  player.addEventListener("error", () => reportFailed(player.error), { once: true });
  player.addEventListener("playing", () => {
    frames(2).then(() => reportShown(details()));
  }, { once: true });
  player.addEventListener("loadeddata", () => {
    setTimeout(() => reportShown(details()), SHOWN_AFTER_LOADED_MS);
  }, { once: true });
  player.src = show.url;
  player.play().catch(() => {});
}

function seek(delta) {
  const limit = Number.isFinite(media.duration) ? media.duration : Infinity;
  media.currentTime = Math.min(limit, Math.max(0, media.currentTime + delta));
  flash(`${delta > 0 ? "+" : "−"}${Math.abs(delta)} s · ${clock(media.currentTime)}`);
}

function volume(delta) {
  media.muted = false;
  media.volume = Math.min(1, Math.max(0, Math.round((media.volume + delta) * 10) / 10));
  flash(`Volume ${Math.round(media.volume * 100)} %`);
}

function onKey(key) {
  if (!media || !reported) {
    return;
  }
  switch (key) {
    case "k":
      if (media.ended) {
        media.currentTime = 0;
      }
      if (media.paused || media.ended) {
        media.play().catch(() => {});
        flash("Playing");
      } else {
        media.pause();
        flash("Paused");
      }
      break;
    case "j":
      seek(-10);
      break;
    case "l":
      seek(10);
      break;
    case "comma":
      seek(-1);
      break;
    case "period":
      seek(1);
      break;
    case "m":
      media.muted = !media.muted;
      flash(media.muted ? "Muted" : "Sound on");
      break;
    case "plus":
    case "shift+plus":
      volume(0.1);
      break;
    case "minus":
      volume(-0.1);
      break;
    default:
      if (/^[0-9]$/.test(key) && Number.isFinite(media.duration)) {
        media.currentTime = (media.duration * Number(key)) / 10;
        flash(`${Number(key) * 10} % · ${clock(media.currentTime)}`);
      }
      break;
  }
}

function start(message) {
  show = message;
  applyTheme(message.theme);
  if (AUDIO_EXTENSIONS.has(message.extension)) {
    startAudio();
  } else {
    startVideo();
  }
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
      if (!show) {
        start(message);
      }
      break;
    case "quickview-theme":
      applyTheme(message.theme);
      break;
    case "quickview-key":
      onKey(message.key);
      break;
    default:
      break;
  }
});

post({ type: "ready" });
