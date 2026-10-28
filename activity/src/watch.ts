import Hls from "hls.js";

import type { Live } from "./main";
import { api, esc, time, toast } from "./ui";

let live: Live | null = null;

// The video: one player kept while the same stream plays, following the room's timeline.
let video: HTMLVideoElement | null = null;
let hls: Hls | null = null;
let playing = "";
let seekingByUs = false;

// What other voice channels watch, to tune in to on your own (watch-only), or the way back.
function elsewhere() {
  const state = live!.state!;
  const w = state.watch;
  const others = state.watchElsewhere ?? [];
  if (w?.tunedTo) return `<div class="tuned">📡 Watching along with <b>#${esc(w.tunedTo)}</b> <button data-tune="">↩ Back to ${esc(state.channel)}</button></div>`;
  if (others.length === 0) return "";
  return `<h3>Watching elsewhere</h3><ul class="elsewhere">${others.map((o) => `<li><span><b>#${esc(o.name)}</b> <span class="muted">· ${esc(o.title)}</span></span><button data-tune="${o.channel}">📡 Tune in</button></li>`).join("")}</ul>`;
}

let shownElsewhere = "";
export function render(l: Live, tabs: string) {
  live = l;
  const { root, state } = l;
  const w = state!.watch;
  const add = `<form id="watch-add"><input type="text" name="q" placeholder="Add a video: a link or search words" maxlength="300" /><button>Add</button></form>`;
  if (!w || !w.stream) {
    leave();
    root.innerHTML = `${tabs}${w?.problem ? `<section class="card wide problem">⚠️ ${esc(w.problem)}</section>` : ""}<section class="card empty"><h1>Nothing to watch in ${esc(state!.channel)} yet</h1><p class="muted">Add a video and everyone here watches it together, in step.</p>${add}${elsewhere()}</section>`;
    wire();
    return;
  }
  if (playing !== w.stream || !video || !document.body.contains(video)) {
    leave();
    root.innerHTML = `${tabs}
      <section class="card player"><video id="video" playsinline></video><button id="fill" title="Fill the window (Esc to leave)">⛶</button>
        <div class="scrub"><span id="watch-at" class="muted">0:00</span><input type="range" id="watch-bar" min="0" max="0" step="1" value="0" aria-label="Position" /><span id="watch-len" class="muted"></span><button id="watch-mute" title="Mute">🔊</button><input type="range" id="watch-vol" min="0" max="1" step="0.05" value="1" aria-label="Volume" /></div></section>
      <section class="card"><div id="watch-elsewhere"></div><p class="muted" id="watch-problem"></p><h1 id="watch-title"></h1><p class="muted" id="watch-by"></p>
        <div id="watch-own"><div class="controls"><button data-watch="toggle" id="watch-toggle"></button><button data-watch="skip">⏭️ Skip</button><button data-watch="stop">⏹️ Stop</button></div>${add}</div></section>
      <section class="card"><h2>Up next</h2><div id="watch-queue"></div></section>`;
    video = document.getElementById("video") as HTMLVideoElement;
    document.getElementById("fill")!.onclick = () => fill(!document.querySelector(".player.filled") && !document.fullscreenElement);
    const source = `${api}/watch/${w.stream}/index.m3u8`;
    if (Hls.isSupported()) {
      hls = new Hls({ startPosition: Math.max(0, w.position) });
      // The stream may only just be starting: try again shortly.
      hls.on(Hls.Events.ERROR, (_, data) => {
        if (!data.fatal || !hls) return;
        streamProblem(`${data.type}: ${data.details}${data.response?.code ? ` (${data.response.code})` : ""}`);
        setTimeout(() => hls?.loadSource(source), 2000);
      });
      hls.on(Hls.Events.FRAG_LOADED, () => streamProblem(null));
      hls.loadSource(source);
      hls.attachMedia(video);
    } else video.src = source;
    playing = w.stream;
    // Pausing or seeking in the player goes to everyone.
    video.addEventListener("pause", () => { if (!live?.state?.watch?.tunedTo && !seekingByUs && !live?.state?.watch?.paused && !video!.ended) live?.send("watch-pause"); });
    video.addEventListener("play", () => { if (!live?.state?.watch?.tunedTo && !seekingByUs && live?.state?.watch?.paused) live?.send("watch-play"); });
    video.addEventListener("seeked", () => { if (!seekingByUs && !live?.state?.watch?.tunedTo) live?.send("watch-seek", video!.currentTime); else seekingByUs = false; });
    // The bar runs over the video's whole length as the bot knows it, not over what the stream has loaded so far.
    const bar = document.getElementById("watch-bar") as HTMLInputElement;
    bar.oninput = () => { document.getElementById("watch-at")!.textContent = time(Number(bar.value) * 1000); };
    bar.onchange = () => { if (!live?.state?.watch?.tunedTo && video) { seekingByUs = true; video.currentTime = Number(bar.value); live?.send("watch-seek", Number(bar.value)); } };
    video.onclick = () => document.getElementById("watch-toggle")?.click();
    const mute = document.getElementById("watch-mute") as HTMLButtonElement;
    const vol = document.getElementById("watch-vol") as HTMLInputElement;
    mute.onclick = () => { video!.muted = !video!.muted; mute.textContent = video!.muted ? "🔇" : "🔊"; };
    vol.oninput = () => { video!.volume = Number(vol.value); video!.muted = false; mute.textContent = "🔊"; };
    wire();
  }
  // Redrawn only when it changes, so the buttons stay pressable.
  const around = elsewhere();
  if (around !== shownElsewhere || !document.getElementById("watch-elsewhere")!.innerHTML) {
    document.getElementById("watch-elsewhere")!.innerHTML = around;
    shownElsewhere = around;
    wire();
  }
  document.getElementById("watch-own")!.hidden = !!w.tunedTo;
  document.getElementById("watch-title")!.textContent = w.title;
  document.getElementById("watch-by")!.textContent = w.uploader ?? "";
  document.getElementById("watch-toggle")!.textContent = w.paused ? "▶️ Play" : "⏸️ Pause";
  const length = w.seconds ?? (video && isFinite(video.duration) ? video.duration : 0);
  (document.getElementById("watch-bar") as HTMLInputElement).max = String(Math.ceil(length));
  document.getElementById("watch-len")!.textContent = length ? time(length * 1000) : "";
  document.getElementById("watch-queue")!.innerHTML = w.queue.length === 0 ? `<p class="muted">Nothing queued.</p>`
    : `<ol>${w.queue.map((q, i) => `<li>${esc(q.title)}${q.seconds ? ` <span class="muted">· ${time(q.seconds * 1000)}</span>` : ""}${q.canRemove ? ` <button class="link" data-watch-remove="${i + 1}" title="Take it out of the queue">✕</button>` : ""}</li>`).join("")}</ol>`;
  document.querySelectorAll<HTMLButtonElement>("button[data-watch-remove]").forEach((b) => (b.onclick = () => live?.send("watch-remove", Number(b.dataset.watchRemove))));
  follow();
}

// Lets the player go: when the tab changes, the stream does, or the page moves to another channel.
export function leave() {
  hls?.destroy();
  hls = null;
  video = null;
  playing = "";
}

function wire() {
  document.querySelectorAll<HTMLButtonElement>("button[data-tune]").forEach((b) => (b.onclick = () => live?.send("watch-tune", b.dataset.tune || null)));
  document.querySelectorAll<HTMLButtonElement>("button[data-watch]").forEach((b) => (b.onclick = () => {
    const action = b.dataset.watch === "toggle" ? (live?.state?.watch?.paused ? "watch-play" : "watch-pause") : `watch-${b.dataset.watch}`;
    live?.send(action);
  }));
  const addVideo = document.getElementById("watch-add") as HTMLFormElement | null;
  if (addVideo) addVideo.onsubmit = (e) => {
    e.preventDefault();
    const q = (addVideo.elements.namedItem("q") as HTMLInputElement).value.trim();
    if (q) { live?.send("watch-add", q); toast("Finding it…"); }
    addVideo.reset();
  };
}

// The video over the whole window: the browser's full screen where allowed (Discord's frame may not), else
// filling the Activity's window.
function fill(on: boolean) {
  const player = document.querySelector<HTMLElement>(".player");
  if (!player) return;
  if (!on) {
    player.classList.remove("filled");
    if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
    return;
  }
  if (player.requestFullscreen) player.requestFullscreen().catch(() => player.classList.add("filled"));
  else player.classList.add("filled");
}
document.addEventListener("keydown", (e) => { if (e.key === "Escape") fill(false); });

// Shown under the video while the stream won't load, and told to the bot's log once per kind.
const reported = new Set<string>();
function streamProblem(problem: string | null) {
  const line = document.getElementById("watch-problem");
  if (line) line.textContent = problem ? `⚠️ The video won't load (${problem}); still trying…` : "";
  if (problem && !reported.has(problem)) {
    reported.add(problem);
    live?.send("client-log", `video ${playing}: ${problem}`);
  }
}

// Where everyone should be now; small drift is caught up by speed, larger by a jump.
function follow() {
  const w = live?.state?.watch;
  if (!w || !video) return;
  const target = w.position + (w.paused ? 0 : (performance.now() - live!.receivedAt) / 1000);
  if (w.paused || target < 0) {
    if (!video.paused) { seekingByUs = true; video.pause(); seekingByUs = false; }
    return;
  }
  const bar = document.getElementById("watch-bar") as HTMLInputElement | null;
  if (bar && document.activeElement !== bar) { bar.value = String(Math.floor(video.currentTime)); document.getElementById("watch-at")!.textContent = time(video.currentTime * 1000); }
  const drift = video.currentTime - target;
  if (Math.abs(drift) > 1.5) {
    seekingByUs = true;
    video.currentTime = target;
  }
  video.playbackRate = Math.abs(drift) > 0.25 ? (drift > 0 ? 0.95 : 1.05) : 1;
  if (video.paused) video.play().catch((error) => {
    toast("Click the video to start it.");
    if (error?.name !== "NotAllowedError") streamProblem(String(error?.message ?? error));
  });
}
setInterval(follow, 1000);
