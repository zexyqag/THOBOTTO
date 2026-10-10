import { DiscordSDK } from "@discord/embedded-app-sdk";
import Hls from "hls.js";
import "./style.css";

// Inside Discord the page is proxied: our own addresses start with /.proxy. Outside (development), it's the
// bot's /activity/ itself.
const params = new URLSearchParams(location.search);
const inDiscord = params.has("frame_id");
const api = inDiscord ? "/.proxy/api" : "/activity/api";
const app = document.getElementById("app")!;

type Song = { title: string; author: string; uri?: string; art?: string; length?: number; by?: string; added: boolean };
type Watched = { title: string; uploader?: string; page: string; seconds?: number; stream?: string; paused: boolean; position: number; queue: { title: string; seconds?: number }[] };
type State = {
  channel: string; helper?: string; paused?: boolean; volume?: number; canControl?: boolean;
  track?: Song; position?: number; queue?: Song[]; lyrics?: { text: string; lines: { at: number; text: string }[] };
  watch?: Watched;
};

// Which tab shows: Watch when a video is on (until someone picks Music).
let tab: "music" | "watch" | null = null;

let state: State | null = null;
let receivedAt = 0;
let socket: WebSocket | null = null;
let shownTrack = "";

async function signIn(): Promise<{ session: string; guild: string; channel: string }> {
  if (!inDiscord) {
    const dev = await (await fetch(`${api}/dev-session?user=${params.get("user")}`)).json();
    return { session: dev.session, guild: params.get("guild")!, channel: params.get("channel")! };
  }
  const { clientId } = await (await fetch(`${api}/config`)).json();
  const sdk = new DiscordSDK(clientId);
  await sdk.ready();
  const { code } = await sdk.commands.authorize({ client_id: clientId, response_type: "code", state: "", prompt: "none", scope: ["identify"] });
  const reply = await fetch(`${api}/token`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ code }) });
  if (!reply.ok) throw new Error((await reply.json()).detail ?? "Signing in failed.");
  const { accessToken, session } = await reply.json();
  await sdk.commands.authenticate({ access_token: accessToken });
  return { session, guild: sdk.guildId!, channel: sdk.channelId! };
}

async function connect() {
  const { session, guild, channel } = await signIn();
  const scheme = location.protocol === "https:" ? "wss" : "ws";
  socket = new WebSocket(`${scheme}://${location.host}${api}/live?session=${encodeURIComponent(session)}&guild=${guild}&channel=${channel}`);
  socket.onmessage = (event) => {
    const message = JSON.parse(event.data);
    if (message.reply) return toast(message.reply);
    state = message;
    receivedAt = performance.now();
    render();
  };
  // Lost (a deploy, a network hiccup): try again shortly.
  socket.onclose = () => setTimeout(() => connect().catch(fail), 3000);
}

function send(action: string, value?: unknown) {
  socket?.send(JSON.stringify({ do: action, value }));
}

// Where the song is now: the last position heard, moved on by the time since (unless paused).
function position(): number {
  if (!state?.track) return 0;
  const moved = state.paused ? 0 : performance.now() - receivedAt;
  return Math.min((state.position ?? 0) + moved, state.track.length ?? Infinity);
}

const time = (ms: number) => `${Math.floor(ms / 60000)}:${String(Math.floor(ms / 1000) % 60).padStart(2, "0")}`;
const esc = (text: string | undefined) => (text ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]!);
const art = (song: Song) => (song.art ? `${api}/art?url=${encodeURIComponent(song.art)}` : "");

function render() {
  if (!state) return;
  const current = tab ?? (state.watch ? "watch" : "music");
  const tabs = `<nav class="tabs">${(["music", "watch"] as const).map((t) => `<button data-tab="${t}" class="${t === current ? "here" : ""}">${t === "music" ? "🎵 Music" : "🎬 Watch"}</button>`).join("")}</nav>`;
  if (current === "watch") return renderWatch(tabs);
  leaveWatch();
  const addForm = `<form id="add"><input type="text" name="q" placeholder="Add a song: search words or a link" maxlength="300" /><button>Add</button></form>`;
  if (!state.track) {
    app.innerHTML = `${tabs}<section class="card empty"><h1>Nothing's playing in ${esc(state.channel)}</h1><p class="muted">Add a song and a helper brings it.</p>${addForm}</section>`;
    wire();
    return;
  }
  const t = state.track;
  const key = `${t.title}\n${t.author}`;
  const lines = state.lyrics?.lines ?? [];
  const lyrics = !state.lyrics ? `<p class="muted">No lyrics found.</p>`
    : lines.length > 0 ? lines.map((l, i) => `<p data-i="${i}">${esc(l.text) || "♪"}</p>`).join("")
    : `<pre>${esc(state.lyrics.text)}</pre>`;
  const queue = state.queue ?? [];
  const added = queue.filter((s) => s.added);
  const later = queue.filter((s) => !s.added);
  const list = (songs: Song[], start: number) => `<ol start="${start}">${songs.map((s) => `<li>${esc(s.title)} <span class="muted">· ${esc(s.author)}${s.by ? ` · ${esc(s.by)}` : ""}</span></li>`).join("")}</ol>`;
  // The lyrics keep their scroll when only the rest changes.
  const scroll = shownTrack === key ? document.querySelector(".lyrics")?.scrollTop ?? 0 : 0;
  app.innerHTML = `${tabs}
    <section class="card now">
      ${t.art ? `<img src="${art(t)}" alt="" />` : `<div class="noart"></div>`}
      <div class="info">
        <h1>${esc(t.title)}</h1>
        <p class="author">${esc(t.author)}</p>
        <p class="muted">${t.by ? `Asked for by ${esc(t.by)} · ` : ""}${esc(state.helper)} in ${esc(state.channel)}</p>
        <div class="bar"><div id="bar"></div></div>
        <div class="times muted"><span id="at"></span><span>${t.length ? time(t.length) : "live"}</span></div>
        ${state.canControl ? `<div class="controls">
          <button data-do="pause">${state.paused ? "▶️ Play" : "⏸️ Pause"}</button>
          <button data-do="skip">⏭️ Skip</button>
          <button data-do="stop">⏹️ Stop</button>
          <label class="muted">🔊 <input type="range" id="volume" min="0" max="200" value="${state.volume}" /></label>
        </div>` : ""}
        ${addForm}
      </div>
    </section>
    <section class="card"><h2>Lyrics</h2><div class="lyrics">${lyrics}</div></section>
    <section class="card"><h2>Up next</h2>
      ${queue.length === 0 ? `<p class="muted">Nothing queued.</p>` : ""}
      ${added.length > 0 ? `<h3>Next in queue</h3>${list(added, 1)}` : ""}
      ${later.length > 0 ? `<h3>Then from playlists</h3>${list(later, added.length + 1)}` : ""}
    </section>`;
  shownTrack = key;
  const box = document.querySelector(".lyrics");
  if (box) box.scrollTop = scroll;
  wire();
  tick();
}

// The video: one player kept while the same stream plays, following the room's timeline.
let video: HTMLVideoElement | null = null;
let hls: Hls | null = null;
let playing = "";
let seekingByUs = false;

function renderWatch(tabs: string) {
  const w = state!.watch;
  const add = `<form id="watch-add"><input type="text" name="q" placeholder="Add a video: a link or search words" maxlength="300" /><button>Add</button></form>`;
  if (!w || !w.stream) {
    leaveWatch();
    app.innerHTML = `${tabs}<section class="card empty"><h1>Nothing to watch in ${esc(state!.channel)} yet</h1><p class="muted">Add a video and everyone here watches it together, in step.</p>${add}</section>`;
    wire();
    return;
  }
  if (playing !== w.stream || !video || !document.body.contains(video)) {
    leaveWatch();
    app.innerHTML = `${tabs}
      <section class="card player"><video id="video" controls playsinline></video></section>
      <section class="card"><h1 id="watch-title"></h1><p class="muted" id="watch-by"></p>
        <div class="controls"><button data-watch="toggle" id="watch-toggle"></button><button data-watch="skip">⏭️ Skip</button><button data-watch="stop">⏹️ Stop</button></div>${add}</section>
      <section class="card"><h2>Up next</h2><div id="watch-queue"></div></section>`;
    video = document.getElementById("video") as HTMLVideoElement;
    const source = `${api}/watch/${w.stream}/index.m3u8`;
    if (Hls.isSupported()) {
      hls = new Hls({ startPosition: Math.max(0, w.position) });
      // The stream may only just be starting: try again shortly.
      hls.on(Hls.Events.ERROR, (_, data) => {
        if (data.fatal && hls) setTimeout(() => hls?.loadSource(source), 2000);
      });
      hls.loadSource(source);
      hls.attachMedia(video);
    } else video.src = source;
    playing = w.stream;
    // Pausing or seeking in the player goes to everyone.
    video.addEventListener("pause", () => { if (!seekingByUs && !state?.watch?.paused && !video!.ended) send("watch-pause"); });
    video.addEventListener("play", () => { if (!seekingByUs && state?.watch?.paused) send("watch-play"); });
    video.addEventListener("seeked", () => { if (!seekingByUs) send("watch-seek", video!.currentTime); else seekingByUs = false; });
    wire();
  }
  document.getElementById("watch-title")!.textContent = w.title;
  document.getElementById("watch-by")!.textContent = w.uploader ?? "";
  document.getElementById("watch-toggle")!.textContent = w.paused ? "▶️ Play" : "⏸️ Pause";
  document.getElementById("watch-queue")!.innerHTML = w.queue.length === 0 ? `<p class="muted">Nothing queued.</p>`
    : `<ol>${w.queue.map((q) => `<li>${esc(q.title)}${q.seconds ? ` <span class="muted">· ${time(q.seconds * 1000)}</span>` : ""}</li>`).join("")}</ol>`;
  follow();
}

function leaveWatch() {
  hls?.destroy();
  hls = null;
  video = null;
  playing = "";
}

// Where everyone should be now; small drift is caught up by speed, larger by a jump.
function follow() {
  const w = state?.watch;
  if (!w || !video) return;
  const target = w.position + (w.paused ? 0 : (performance.now() - receivedAt) / 1000);
  if (w.paused || target < 0) {
    if (!video.paused) { seekingByUs = true; video.pause(); seekingByUs = false; }
    return;
  }
  const drift = video.currentTime - target;
  if (Math.abs(drift) > 1.5) {
    seekingByUs = true;
    video.currentTime = target;
  }
  video.playbackRate = Math.abs(drift) > 0.25 ? (drift > 0 ? 0.95 : 1.05) : 1;
  if (video.paused) video.play().catch(() => toast("Click the video to start it."));
}
setInterval(follow, 1000);

function wire() {
  document.querySelectorAll<HTMLButtonElement>("button[data-tab]").forEach((b) => (b.onclick = () => { tab = b.dataset.tab as "music" | "watch"; render(); }));
  document.querySelectorAll<HTMLButtonElement>("button[data-watch]").forEach((b) => (b.onclick = () => {
    const action = b.dataset.watch === "toggle" ? (state?.watch?.paused ? "watch-play" : "watch-pause") : `watch-${b.dataset.watch}`;
    send(action);
  }));
  const addVideo = document.getElementById("watch-add") as HTMLFormElement | null;
  if (addVideo) addVideo.onsubmit = (e) => {
    e.preventDefault();
    const q = (addVideo.elements.namedItem("q") as HTMLInputElement).value.trim();
    if (q) { send("watch-add", q); toast("Finding it…"); }
    addVideo.reset();
  };
  document.querySelectorAll<HTMLButtonElement>("button[data-do]").forEach((b) => (b.onclick = () => send(b.dataset.do!)));
  const volume = document.getElementById("volume") as HTMLInputElement | null;
  if (volume) volume.onchange = () => send("volume", Number(volume.value));
  const add = document.getElementById("add") as HTMLFormElement | null;
  if (add) add.onsubmit = (e) => {
    e.preventDefault();
    const q = (add.elements.namedItem("q") as HTMLInputElement).value.trim();
    if (q) send("add", q);
    add.reset();
  };
}

// The bar, the time and the lyric line that's being sung, a few times a second.
let lastLine = -1;
function tick() {
  if (!state?.track) return;
  const at = position();
  const bar = document.getElementById("bar");
  if (bar && state.track.length) bar.style.width = `${(at / state.track.length) * 100}%`;
  const shown = document.getElementById("at");
  if (shown) shown.textContent = time(at);
  const lines = state.lyrics?.lines ?? [];
  let current = -1;
  for (let i = 0; i < lines.length && lines[i].at <= at; i++) current = i;
  if (current !== lastLine || !document.querySelector(".lyrics p.now")) {
    document.querySelectorAll(".lyrics p.now").forEach((p) => p.classList.remove("now"));
    const line = document.querySelector<HTMLElement>(`.lyrics p[data-i="${current}"]`);
    if (line) {
      line.classList.add("now");
      const box = line.parentElement!;
      box.scrollTop = line.offsetTop - box.offsetTop - box.clientHeight / 3;
    }
    lastLine = current;
  }
}
setInterval(tick, 250);

function toast(text: string) {
  const note = document.createElement("div");
  note.className = "toast";
  note.textContent = text.replace(/\*\*|\[|\]\([^)]*\)/g, "");
  document.body.append(note);
  setTimeout(() => note.remove(), 3500);
}

function fail(error: unknown) {
  app.innerHTML = `<section class="card"><h1>Couldn't connect</h1><p class="muted">${esc(String(error instanceof Error ? error.message : error))}</p></section>`;
}

connect().catch(fail);
