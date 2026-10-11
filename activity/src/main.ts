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
type Watched = { problem?: string; tunedTo?: string; title: string; uploader?: string; page: string; seconds?: number; stream?: string; paused: boolean; position: number; queue: { title: string; seconds?: number }[] };
type State = {
  channel: string; helper?: string; paused?: boolean; volume?: number; canControl?: boolean; canVolume?: boolean;
  track?: Song; position?: number; queue?: Song[]; lyrics?: { text: string; lines: { at: number; text: string }[] };
  on?: { watch: boolean; quiz: boolean; sounds: boolean; wrapped: boolean };
  watch?: Watched; watchElsewhere?: { channel: string; name: string; title: string }[]; captions?: Captions; quiz?: Quiz; poll?: Poll; skipVotes?: { votes: number; needed: number };
};
type Poll = { question: string; options: { text: string; votes: number }[]; mine?: number; closed: boolean; left: number; asker: boolean };
type Quiz = {
  phase: "starting" | "asking" | "reveal" | "over"; round: number; rounds: number; left: number; choices: string[]; mine?: number;
  answered: number; players: number; correct?: number; song?: string; art?: string; right: { name: string; gain: number }[]; gain?: number;
  scores: { name: string; score: number; me: boolean }[]; canStop: boolean;
};
type Captions = { listening: boolean; heard: boolean; canQuote: boolean; lines: { id: number; name: string; text: string; at: number; quotable: boolean }[] };

// Which tab shows: Watch when a video is on (until someone picks another).
type Tab = "music" | "watch" | "quiz" | "poll" | "sounds" | "wrapped" | "captions";
let tab: Tab | null = null;
const tabNames: Record<Tab, string> = { music: "🎵 Music", watch: "🎬 Watch", quiz: "🎯 Quiz", poll: "📊 Poll", sounds: "🔊 Sounds", wrapped: "✨ Wrapped", captions: "💬 Captions" };

// Caption lines picked for a quote.
const picked = new Set<number>();

let state: State | null = null;
let receivedAt = 0;
let socket: WebSocket | null = null;
let failures = 0;
let shownTrack = "";

type SignedIn = { session: string; guild: string; channel: string };
// In a plain browser (not Discord, not development): signed in to the panel, following their voice channel.
const webMode = !inDiscord && !params.has("user");
let sdk: DiscordSDK | null = null;
let panel: string | null = null;
let inDiscordSignedIn: SignedIn | null = null;
let where = "";

async function webSession(): Promise<{ session: string; voice: { guild: string; channel: string } | null } | null> {
  const reply = await fetch(`${api}/web-session`);
  if (reply.status === 401) {
    location.href = `/login?back=${encodeURIComponent(location.pathname)}`;
    return new Promise(() => {});
  }
  return reply.ok ? reply.json() : null;
}

async function signIn(): Promise<SignedIn> {
  if (webMode) {
    for (;;) {
      const web = await webSession();
      if (web?.voice) {
        where = `${web.voice.guild}/${web.voice.channel}`;
        return { session: web.session, ...web.voice };
      }
      where = "";
      app.innerHTML = `<section class="card empty"><h1>Join a voice channel</h1><p class="muted">This page follows you: join a voice channel in a server with THOBOTTO, and what's going on there shows up here.</p></section>`;
      await new Promise((done) => setTimeout(done, 5000));
    }
  }
  if (!inDiscord) {
    const dev = await (await fetch(`${api}/dev-session?user=${params.get("user")}`)).json();
    return { session: dev.session, guild: params.get("guild")!, channel: params.get("channel")! };
  }
  if (inDiscordSignedIn) return inDiscordSignedIn;
  const config = await (await fetch(`${api}/config`)).json();
  const clientId = config.clientId;
  panel = config.panel;
  sdk = new DiscordSDK(clientId);
  await sdk.ready();
  const { code } = await sdk.commands.authorize({ client_id: clientId, response_type: "code", state: "", prompt: "none", scope: ["identify"] });
  const reply = await fetch(`${api}/token`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ code }) });
  if (!reply.ok) throw new Error((await reply.json()).detail ?? "Signing in failed.");
  const { accessToken, session } = await reply.json();
  await sdk.commands.authenticate({ access_token: accessToken });
  return (inDiscordSignedIn = { session, guild: sdk.guildId!, channel: sdk.channelId! });
}

// In a browser: moving to another voice channel takes the page along.
if (webMode)
  setInterval(async () => {
    const web = await webSession().catch(() => null);
    const now = web?.voice ? `${web.voice.guild}/${web.voice.channel}` : "";
    if (web && now !== where) {
      where = now;
      state = null;
      leaveWatch();
      socket?.close();
    }
  }, 10000);

async function connect() {
  const { session, guild, channel } = await signIn();
  const scheme = location.protocol === "https:" ? "wss" : "ws";
  socket = new WebSocket(`${scheme}://${location.host}${api}/live?session=${encodeURIComponent(session)}&guild=${guild}&channel=${channel}`);
  socket.onopen = () => {
    failures = 0;
    // Asked before a reconnect: ask again.
    if (wrappedLoading) wrappedAsked = false;
    if (!soundList) soundsAsked = false;
  };
  socket.onmessage = (event) => {
    const message = JSON.parse(event.data);
    if (message.reply) {
      // An action that failed while a Wrapped was being fetched: stop waiting for it.
      if (wrappedLoading && message.reply.startsWith("That didn't work")) { wrappedLoading = false; shownStory = ""; render(); }
      return toast(message.reply);
    }
    if (message.wrapped) return showWrapped(message.wrapped);
    if (message.sounds) { soundList = message.sounds; return render(); }
    state = message;
    receivedAt = performance.now();
    render();
  };
  // Lost (a deploy, a network hiccup): try again shortly.
  socket.onclose = () => {
    // Never got through: say why it may be, and keep trying.
    if (++failures >= 3 && !state)
      app.innerHTML = `<section class="card"><h1>Can't reach the bot live</h1><p class="muted">Signed in, but the live connection doesn't get through. If the bot is behind a reverse proxy, turn on WebSocket support for its address (in Nginx Proxy Manager: the proxy host → Websockets Support). Still trying…</p></section>`;
    setTimeout(() => connect().catch(fail), 3000);
  };
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

// What's typed (and where the cursor is) stays through redrawing on each live update.
function render() {
  if (!state) return;
  const fields = () => [...app.querySelectorAll<HTMLInputElement | HTMLTextAreaElement>("input[type=text], textarea")];
  const name = (f: Element) => `${f.closest("form")?.id ?? ""}:${f.getAttribute("name") ?? f.id}`;
  const typed = fields().map((f) => ({ name: name(f), value: f.value, focused: f === document.activeElement, caret: f.selectionStart }));
  draw();
  for (const field of fields()) {
    const was = typed.find((t) => t.name === name(field));
    if (!was) continue;
    if (was.value && !field.value) field.value = was.value;
    if (was.focused && document.activeElement !== field) {
      field.focus();
      if (was.caret !== null) field.setSelectionRange(was.caret, was.caret);
    }
  }
}

const shown = (t: Tab) => t === "captions" ? !!state!.captions : t === "music" || t === "poll" || state!.on?.[t] !== false;

function draw() {
  if (!state) return;
  app.classList.remove("fit");
  if (tab && !shown(tab)) tab = null;
  const current = tab ?? (state.quiz ? "quiz" : state.watch ? "watch" : "music");
  const shownTabs = (Object.keys(tabNames) as Tab[]).filter(shown);
  const tabs = `<nav class="tabs">${shownTabs.map((t) => `<button data-tab="${t}" class="${t === current ? "here" : ""}">${tabNames[t]}</button>`).join("")}<button id="feedback" class="pop" title="Tell the bot's owner what works, what doesn't, or what you'd like">💬 Feedback</button>${inDiscord && panel ? `<button id="pop" class="pop" title="The same page in your browser, without Discord around it">↗ Open in browser</button>` : ""}</nav>`;
  if (current === "watch") return renderWatch(tabs);
  leaveWatch();
  if (current === "captions") return renderCaptions(tabs);
  if (current === "quiz") return renderQuiz(tabs);
  if (current === "wrapped") return renderWrapped(tabs);
  if (current === "poll") return renderPoll(tabs);
  if (current === "sounds") return renderSounds(tabs);
  if (karaoke && state.track) return renderKaraoke(tabs);
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
          ${state.canVolume ? `<label class="muted">🔊 <input type="range" id="volume" min="0" max="200" value="${state.volume}" /></label>` : ""}
          <button id="karaoke">🎤 Karaoke</button>
        </div>` : `<div class="controls"><button data-do="skip">🗳️ Vote to skip</button><button id="karaoke">🎤 Karaoke</button></div>`}
        ${skipBar()}
        ${addForm}
      </div>
    </section>
    <section class="card"><h2>Lyrics</h2><div class="lyrics">${lyrics}</div></section>
    <section class="card"><h2>Up next</h2><div class="upnext">
      ${queue.length === 0 ? `<p class="muted">Nothing queued.</p>` : ""}
      ${added.length > 0 ? `<h3>Next in queue</h3>${list(added, 1)}` : ""}
      ${later.length > 0 ? `<h3>Then from playlists</h3>${list(later, added.length + 1)}` : ""}
    </div></section>`;
  shownTrack = key;
  app.classList.add("fit");
  const box = document.querySelector(".lyrics");
  if (box) box.scrollTop = scroll;
  wire();
  tick();
}

// How many voted to skip, of how many it takes.
function skipBar() {
  const s = state?.skipVotes;
  if (!s || s.votes === 0) return "";
  return `<div class="votes"><div class="bar"><div style="width: ${Math.min(100, (s.votes / s.needed) * 100)}%"></div></div><span class="muted">🗳️ ${s.votes} of ${s.needed} to skip</span></div>`;
}

// Karaoke: the line being sung large, the next ones after it.
let karaoke = false;
let shownKaraoke = "";
function renderKaraoke(tabs: string) {
  // Drawn again only for another song (or tabs), so the line in view stays put.
  const key = `${tabs}|${state!.track!.title}|${state!.lyrics?.lines.length ?? -1}`;
  if (key === shownKaraoke && document.querySelector(".karaoke")) return;
  shownKaraoke = key;
  const synced = (state!.lyrics?.lines.length ?? 0) > 0;
  app.innerHTML = `${tabs}<section class="card wide karaoke">
      <div class="karaoke-head"><span class="muted">${esc(state!.track!.title)} · ${esc(state!.track!.author)}</span><button id="karaoke">✖️ Leave karaoke</button></div>
      ${synced ? `<p class="before" id="k-before"></p><p class="sing" id="k-now"></p><p class="after" id="k-next"></p><p class="after" id="k-later"></p>`
        : `<p class="sing">${state!.lyrics ? "These lyrics aren't timed, so they can't follow along." : "No lyrics found for this one."}</p>`}
      <div class="bar"><div id="bar"></div></div>
    </section>`;
  lastKaraoke = -2;
  wire();
  tick();
}
let lastKaraoke = -2;

// A poll for those in the channel: one question, a few answers, live bars.
function renderPoll(tabs: string) {
  const p = state!.poll;
  const inChannel = !!state!.captions;
  const total = p ? p.options.reduce((n, o) => n + o.votes, 0) : 0;
  const poll = p ? `<section class="card wide"><div class="quiz-head"><h1>${esc(p.question)}</h1>${p.asker && !p.closed ? `<button data-poll="close">Close it</button>` : ""}</div>
      <div class="poll">${p.options.map((o, i) => {
        const share = total ? Math.round((o.votes / total) * 100) : 0;
        return `<button data-vote="${i}" class="${p.mine === i ? "mine" : ""}" ${p.closed || !inChannel ? "disabled" : ""}><span class="fill" style="width: ${share}%"></span><span class="text">${esc(o.text)}</span><span class="count">${o.votes} · ${share}%</span></button>`;
      }).join("")}</div>
      <p class="muted">${total} vote${total === 1 ? "" : "s"} · ${p.closed ? "closed" : `closes in <span id="poll-left"></span>`}${p.mine !== undefined && p.mine !== null && !p.closed ? " · tap another to change yours" : ""}</p></section>` : "";
  const form = (!p || p.closed) && inChannel ? `<section class="card wide"><h2>Ask everyone here</h2>
      <form id="poll-new" class="poll-new">
        <input type="text" name="question" placeholder="The question" maxlength="200" required />
        ${[0, 1, 2, 3].map((i) => `<input type="text" name="option" placeholder="Answer ${i + 1}${i > 1 ? " (optional)" : ""}" maxlength="80" ${i < 2 ? "required" : ""} />`).join("")}
        <label class="muted">Open for <select name="minutes">${[1, 2, 5, 10, 30].map((m) => `<option value="${m}" ${m === 5 ? "selected" : ""}>${m} min</option>`).join("")}</select></label>
        <button>📊 Ask</button>
      </form></section>` : !inChannel ? `<section class="card wide"><p class="muted">Join the voice channel to take part.</p></section>` : "";
  // What's typed for a new poll stays through the live updates.
  const typed = [...document.querySelectorAll<HTMLInputElement | HTMLSelectElement>("#poll-new [name]")].map((f) => f.value);
  const focused = [...document.querySelectorAll("#poll-new [name]")].indexOf(document.activeElement!);
  app.innerHTML = `${tabs}${poll}${form}`;
  const fields = [...document.querySelectorAll<HTMLInputElement | HTMLSelectElement>("#poll-new [name]")];
  if (fields.length === typed.length) fields.forEach((f, i) => (f.value = typed[i]));
  if (focused >= 0) fields[focused]?.focus();
  document.querySelectorAll<HTMLButtonElement>("button[data-vote]").forEach((b) => (b.onclick = () => send("poll-vote", Number(b.dataset.vote))));
  document.querySelector<HTMLButtonElement>("button[data-poll=close]")?.addEventListener("click", () => send("poll-close"));
  const create = document.getElementById("poll-new") as HTMLFormElement | null;
  if (create) create.onsubmit = (e) => {
    e.preventDefault();
    const data = new FormData(create);
    send("poll-create", { question: String(data.get("question")), options: data.getAll("option").map(String).filter((o) => o.trim()), minutes: Number(data.get("minutes")) });
    create.reset();
    (document.activeElement as HTMLElement | null)?.blur();
  };
  wire();
  pollTick();
}
function pollTick() {
  const shown = document.getElementById("poll-left");
  if (!shown || !state?.poll) return;
  const left = Math.max(0, state.poll.left - (performance.now() - receivedAt));
  shown.textContent = time(left);
}
setInterval(pollTick, 500);

// The server's soundboard: tap one and a helper plays it in the channel.
let soundList: { off?: string; list?: { name: string; seconds: number }[] } | null = null;
let soundsAsked = false;
let soundFilter = "";
function renderSounds(tabs: string) {
  if (!soundsAsked) { soundsAsked = true; send("sounds"); }
  if (document.activeElement?.id === "sound-filter" && document.querySelector(".soundgrid")) return drawSounds();
  const body = !soundList ? `<p class="muted">Getting the sounds…</p>`
    : soundList.off ? `<p class="muted">${plain(soundList.off)}</p>`
    : `<input type="text" id="sound-filter" placeholder="Find a sound" value="${esc(soundFilter)}" /><div class="soundgrid"></div>`;
  app.innerHTML = `${tabs}<section class="card wide"><div class="quiz-head"><h2>Soundboard</h2><button id="sounds-refresh">↻</button></div>${body}</section>`;
  const filter = document.getElementById("sound-filter") as HTMLInputElement | null;
  if (filter) filter.oninput = () => { soundFilter = filter.value; drawSounds(); };
  document.getElementById("sounds-refresh")!.onclick = () => send("sounds");
  drawSounds();
  wire();
}
function drawSounds() {
  const grid = document.querySelector(".soundgrid");
  if (!grid || !soundList?.list) return;
  const shown = soundList.list.filter((s) => s.name.toLowerCase().includes(soundFilter.toLowerCase()));
  grid.innerHTML = shown.length === 0 ? `<p class="muted">${soundList.list.length === 0 ? "No sounds yet: they're added on the panel's Sounds page or with /sound." : "None match."}</p>`
    : shown.map((s) => `<button data-sound="${esc(s.name)}">🔊 ${esc(s.name)}<span class="muted">${s.seconds}s</span></button>`).join("");
  grid.querySelectorAll<HTMLButtonElement>("button[data-sound]").forEach((b) => (b.onclick = () => send("sound-play", b.dataset.sound)));
}

// Wrapped as stories: one slide at a time, on by itself; tap the right side for the next, the left to go back.
type Card = { title: string; intro?: string; color?: number; facts: { label: string; value: string }[]; lists: { heading: string; lines: string[] }[] };
type Wrapped = { off?: string; who: "me" | "server"; period: string; periods: string[]; cards: Card[] };
type Slide = { color?: number; html: string };
const periodNames: Record<string, string> = { ThisMonth: "This month", LastMonth: "Last month", ThisYear: "This year", LastYear: "Last year", AllTime: "All time" };
const SlideMs = 6000;
let wrapped: Wrapped | null = null;
let wrappedAsked = false;
let slides: Slide[] = [];
let slide = 0;
let slideAt = 0;
let storyPaused = false;
let wrappedSeq = 0;
let shownStory = "";

// Discord's bold and list numbers don't show here.
const plain = (text: string) => esc(text.replace(/\*\*|__|`/g, "").replace(/^\d+\.\s+/, ""));

let wrappedLoading = false;
function askWrapped(who: string, period?: string) {
  wrappedAsked = true;
  wrappedLoading = true;
  shownStory = "";
  send("wrapped", { who, period });
  render();
}

function showWrapped(w: Wrapped) {
  wrapped = w;
  wrappedLoading = false;
  wrappedSeq++;
  slides = [];
  for (const card of w.cards) {
    slides.push({ color: card.color, html: `<h1>${plain(card.title)}</h1>${card.intro ? `<p class="intro">${plain(card.intro)}</p>` : ""}` });
    for (let i = 0; i < card.facts.length; i += 2)
      slides.push({ color: card.color, html: card.facts.slice(i, i + 2).map((f) => `<div class="fact"><div class="value">${plain(f.value)}</div><div class="label">${plain(f.label)}</div></div>`).join("") });
    for (const list of card.lists)
      slides.push({ color: card.color, html: `<h2>${plain(list.heading)}</h2><ol>${list.lines.map((l) => `<li>${plain(l)}</li>`).join("")}</ol>` });
  }
  slide = 0;
  slideAt = performance.now();
  storyPaused = false;
  render();
}

function renderWrapped(tabs: string) {
  if (!wrappedAsked) askWrapped("me");
  // The live updates don't touch the story, so its slide isn't drawn again.
  const key = `${tabs}|${wrappedSeq}|${slide}|${storyPaused}|${wrappedLoading}`;
  if (key === shownStory && document.querySelector(".story, .card.wide p.muted")) return;
  shownStory = key;
  const w = wrapped;
  const pick = w && !w.off ? `<div class="controls">
      <button data-who="me" class="${w.who === "me" ? "here" : ""}">You</button><button data-who="server" class="${w.who === "server" ? "here" : ""}">The server</button>
      <select id="period">${w.periods.map((p) => `<option value="${p}" ${p === w.period ? "selected" : ""}>${periodNames[p] ?? p}</option>`).join("")}</select>
      <button id="story-pause">${storyPaused ? "▶️" : "⏸️"}</button></div>` : "";
  const s = slides[slide];
  const hue = (n: number) => `#${n.toString(16).padStart(6, "0")}`;
  const ground = s?.color ? `linear-gradient(160deg, ${hue(s.color)}, #1e1f22 85%)` : `linear-gradient(160deg, hsl(${(slide * 47) % 360} 55% 35%), #1e1f22 85%)`;
  const story = !w || wrappedLoading ? `<p class="muted">Getting the Wrapped…</p>`
    : w.off ? `<p class="muted">${plain(w.off)}</p>`
    : !s ? `<p class="muted">Nothing to show for then.</p>`
    : `<div class="story" style="background: ${ground}">
        <div class="segments">${slides.map((_, i) => `<span><i id="seg-${i}" style="width: ${i < slide ? 100 : 0}%"></i></span>`).join("")}</div>
        <div class="slide">${s.html}</div>
        <button class="back" aria-label="Back"></button><button class="next" aria-label="Next"></button>
      </div>`;
  app.innerHTML = `${tabs}<section class="card wide">${pick}${story}</section>`;
  document.querySelectorAll<HTMLButtonElement>("button[data-who]").forEach((b) => (b.onclick = () => askWrapped(b.dataset.who!, wrapped?.period)));
  const period = document.getElementById("period") as HTMLSelectElement | null;
  if (period) period.onchange = () => askWrapped(wrapped!.who, period.value);
  const pause = document.getElementById("story-pause");
  if (pause) pause.onclick = () => { storyPaused = !storyPaused; slideAt = performance.now() - storyAt; render(); };
  const go = (step: number) => { slide = Math.max(0, Math.min(slides.length - 1, slide + step)); slideAt = performance.now(); render(); };
  document.querySelector<HTMLButtonElement>(".story .back")?.addEventListener("click", () => go(-1));
  document.querySelector<HTMLButtonElement>(".story .next")?.addEventListener("click", () => go(1));
  wire();
}

// How far into the slide (ms), and on to the next when it's time.
let storyAt = 0;
function storyTick() {
  if (!slides.length || !document.querySelector(".story")) return;
  storyAt = storyPaused ? storyAt : performance.now() - slideAt;
  const bar = document.getElementById(`seg-${slide}`);
  if (bar) bar.style.width = `${Math.min(100, (storyAt / SlideMs) * 100)}%`;
  if (!storyPaused && storyAt >= SlideMs && slide < slides.length - 1) {
    slide++;
    slideAt = performance.now();
    render();
  }
}
setInterval(storyTick, 100);

// The music quiz: a clip plays, everyone picks its name from four; quicker right answers score more.
function renderQuiz(tabs: string) {
  const q = state!.quiz;
  const inChannel = !!state!.captions;
  const scores = (list: Quiz["scores"]) => list.length === 0 ? "" : `<ol class="scores">${list.map((s, i) => `<li class="${s.me ? "me" : ""}"><span class="place">${s.score > 0 ? ["🥇", "🥈", "🥉"][i] ?? `${i + 1}.` : `${i + 1}.`}</span> ${esc(s.name)} <b>${s.score}</b></li>`).join("")}</ol>`;
  if (!q || q.phase === "over") {
    app.innerHTML = `${tabs}
      ${q ? `<section class="card wide"><h1>🏁 Final standings</h1>${scores(q.scores) || `<p class="muted">Nobody answered.</p>`}</section>` : ""}
      <section class="card wide empty"><h1>🎯 Music quiz</h1>
        <p class="muted">A helper plays a bit of a song; everyone here picks its name from four. Quicker right answers score more. The music waits and goes on after.</p>
        ${inChannel ? `<div class="controls"><button data-quiz="start">▶️ Start a quiz</button></div>` : `<p class="muted">Join the voice channel to play.</p>`}
      </section>`;
    wire();
    return;
  }
  const head = q.phase === "starting" ? `Get ready: ${q.rounds} songs` : `Song ${q.round} of ${q.rounds}`;
  const revealed = q.phase === "reveal";
  const choices = q.phase === "starting" ? `<p class="muted">The first clip starts in <span id="quiz-left"></span>…</p>`
    : `<div class="choices">${q.choices.map((c, i) => {
        const look = revealed ? (i === q.correct ? "right" : i === q.mine ? "wrong" : "") : i === q.mine ? "mine" : "";
        return `<button data-answer="${i}" class="${look}" ${q.mine !== undefined && q.mine !== null || revealed || !inChannel ? "disabled" : ""}>${esc(c)}</button>`;
      }).join("")}</div>`;
  const status = q.phase === "asking" ? `<p class="muted">${q.answered} of ${q.players} answered${q.mine !== undefined && q.mine !== null ? " · locked in" : ""}</p>`
    : revealed ? `<div class="reveal">${q.art ? `<img src="${api}/art?url=${encodeURIComponent(q.art)}" alt="" />` : ""}<div>
        <h2>${esc(q.song)}</h2>
        <p>${q.gain ? `✅ +${q.gain}` : q.mine !== undefined && q.mine !== null ? "❌ Not this time" : "⏱️ No answer"}</p>
        <p class="muted">${q.right.length === 0 ? "Nobody got it." : q.right.map((r) => `${esc(r.name)} +${r.gain}`).join(" · ")}</p></div></div>` : "";
  app.innerHTML = `${tabs}
    <section class="card wide quiz"><div class="quiz-head"><h1>${head}</h1>${q.canStop ? `<button data-quiz="stop">⏹️ Stop</button>` : ""}</div>
      ${q.phase === "asking" ? `<div class="bar"><div id="quiz-bar"></div></div>` : ""}
      ${choices}${status}</section>
    ${q.scores.length > 0 ? `<section class="card wide"><h2>Scores</h2>${scores(q.scores)}</section>` : ""}`;
  quizTotal = q.phase === "asking" && quizRound !== q.round ? q.left : quizTotal;
  quizRound = q.phase === "asking" ? q.round : quizRound;
  document.querySelectorAll<HTMLButtonElement>("button[data-answer]").forEach((b) => (b.onclick = () => send("quiz-answer", Number(b.dataset.answer))));
  wire();
  quizTick();
}

// The time left in the round, between updates.
let quizTotal = 1;
let quizRound = 0;
function quizTick() {
  const q = state?.quiz;
  if (!q) return;
  const left = Math.max(0, q.left - (performance.now() - receivedAt));
  const shown = document.getElementById("quiz-left");
  if (shown) shown.textContent = String(Math.ceil(left / 1000));
  const bar = document.getElementById("quiz-bar");
  if (bar) bar.style.width = `${(left / quizTotal) * 100}%`;
}
setInterval(quizTick, 100);

// What's said in the call, as the helpers hear it; lines tapped become a quote.
function renderCaptions(tabs: string) {
  const c = state!.captions;
  if (!c) {
    app.innerHTML = `${tabs}<section class="card empty"><h1>Captions are for those in ${esc(state!.channel)}</h1><p class="muted">Join the voice channel to see them.</p></section>`;
    wire();
    return;
  }
  const kept = new Set(c.lines.map((l) => l.id));
  picked.forEach((id) => { if (!kept.has(id)) picked.delete(id); });
  const box = document.querySelector(".captions");
  // Following along unless scrolled up to read.
  const atEnd = !box || box.scrollHeight - box.scrollTop - box.clientHeight < 40;
  const scroll = box?.scrollTop ?? 0;
  const notes = [
    !c.listening ? `<p class="muted">No helper is listening here. <button data-do="listen">🎙️ Bring a listener</button></p>` : "",
    !c.heard ? `<p class="muted">You aren't captioned: only members who let the helpers hear them are (<code>/me voice-commands on</code>).</p>` : "",
  ].join("");
  const lines = c.lines.length === 0 ? `<p class="muted">Nothing heard yet. Captions show what's said here for ten minutes, then they're gone; nothing is stored.</p>`
    : c.lines.map((l) => `<p data-line="${l.id}" class="${picked.has(l.id) ? "picked" : ""} ${l.quotable ? "" : "shy"}" title="${l.quotable ? "" : "Rather not be quoted"}"><span class="when muted">${new Date(l.at).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}</span> <b>${esc(l.name)}</b> ${esc(l.text)}</p>`).join("");
  const quoting = c.canQuote ? `<div class="controls"><button id="save-quote" ${picked.size === 0 ? "disabled" : ""}>💬 Save quote${picked.size > 0 ? ` (${picked.size})` : ""}</button>${picked.size > 0 ? `<button id="clear-quote">✖️ Clear</button>` : ""}<span class="muted">Tap lines to pick them.</span></div>` : "";
  app.innerHTML = `${tabs}<section class="card wide"><h2>Captions in ${esc(state!.channel)}</h2>${notes}<div class="captions">${lines}</div>${quoting}</section>`;
  const shown = document.querySelector(".captions")!;
  shown.scrollTop = atEnd ? shown.scrollHeight : scroll;
  if (c.canQuote)
    shown.querySelectorAll<HTMLElement>("p[data-line]:not(.shy)").forEach((p) => (p.onclick = () => {
      const id = Number(p.dataset.line);
      if (!picked.delete(id)) picked.add(id);
      render();
    }));
  const save = document.getElementById("save-quote");
  if (save) save.onclick = () => { send("quote", [...picked]); picked.clear(); render(); };
  const clear = document.getElementById("clear-quote");
  if (clear) clear.onclick = () => { picked.clear(); render(); };
  wire();
}

// The video: one player kept while the same stream plays, following the room's timeline.
let video: HTMLVideoElement | null = null;
let hls: Hls | null = null;
let playing = "";
let seekingByUs = false;

// What other voice channels watch, to tune in to on your own (watch-only), or the way back.
function elsewhere() {
  const w = state!.watch;
  const others = state!.watchElsewhere ?? [];
  if (w?.tunedTo) return `<div class="tuned">📡 Watching along with <b>#${esc(w.tunedTo)}</b> <button data-tune="">↩ Back to ${esc(state!.channel)}</button></div>`;
  if (others.length === 0) return "";
  return `<h3>Watching elsewhere</h3><ul class="elsewhere">${others.map((o) => `<li><span><b>#${esc(o.name)}</b> <span class="muted">· ${esc(o.title)}</span></span><button data-tune="${o.channel}">📡 Tune in</button></li>`).join("")}</ul>`;
}

let shownElsewhere = "";
function renderWatch(tabs: string) {
  const w = state!.watch;
  const add = `<form id="watch-add"><input type="text" name="q" placeholder="Add a video: a link or search words" maxlength="300" /><button>Add</button></form>`;
  if (!w || !w.stream) {
    leaveWatch();
    app.innerHTML = `${tabs}${w?.problem ? `<section class="card wide problem">⚠️ ${esc(w.problem)}</section>` : ""}<section class="card empty"><h1>Nothing to watch in ${esc(state!.channel)} yet</h1><p class="muted">Add a video and everyone here watches it together, in step.</p>${add}${elsewhere()}</section>`;
    wire();
    return;
  }
  if (playing !== w.stream || !video || !document.body.contains(video)) {
    leaveWatch();
    app.innerHTML = `${tabs}
      <section class="card player"><video id="video" controls playsinline></video><button id="fill" title="Fill the window (Esc to leave)">⛶</button></section>
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
    video.addEventListener("pause", () => { if (!state?.watch?.tunedTo && !seekingByUs && !state?.watch?.paused && !video!.ended) send("watch-pause"); });
    video.addEventListener("play", () => { if (!state?.watch?.tunedTo && !seekingByUs && state?.watch?.paused) send("watch-play"); });
    video.addEventListener("seeked", () => { if (!seekingByUs && !state?.watch?.tunedTo) send("watch-seek", video!.currentTime); else seekingByUs = false; });
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
    send("client-log", `video ${playing}: ${problem}`);
  }
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
  if (video.paused) video.play().catch((error) => {
    toast("Click the video to start it.");
    if (error?.name !== "NotAllowedError") streamProblem(String(error?.message ?? error));
  });
}
setInterval(follow, 1000);

function wire() {
  document.querySelectorAll<HTMLButtonElement>("button[data-tab]").forEach((b) => (b.onclick = () => { tab = b.dataset.tab as Tab; render(); }));
  document.querySelectorAll<HTMLButtonElement>("button[data-tune]").forEach((b) => (b.onclick = () => send("watch-tune", b.dataset.tune || null)));
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
  const feedback = document.getElementById("feedback");
  if (feedback) feedback.onclick = askFeedback;
  const pop = document.getElementById("pop");
  if (pop) pop.onclick = () => sdk?.commands.openExternalLink({ url: `${panel}/activity/` }).catch(() => toast(`Open ${panel}/activity/ in your browser.`));
  const sing = document.getElementById("karaoke");
  if (sing) sing.onclick = () => { karaoke = !karaoke; render(); };
  document.querySelectorAll<HTMLButtonElement>("button[data-quiz]").forEach((b) => (b.onclick = () => send(`quiz-${b.dataset.quiz}`)));
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
  const now = document.getElementById("k-now");
  if (now && current !== lastKaraoke) {
    const text = (i: number) => (i >= 0 && i < lines.length ? lines[i].text || "♪" : "");
    document.getElementById("k-before")!.textContent = text(current - 1);
    now.textContent = current < 0 ? "♪" : text(current);
    document.getElementById("k-next")!.textContent = text(current + 1);
    document.getElementById("k-later")!.textContent = text(current + 2);
    now.classList.remove("pop");
    void now.offsetWidth;
    now.classList.add("pop");
    lastKaraoke = current;
  }
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

// A box over the page (outside what's redrawn), so typing isn't interrupted.
function askFeedback() {
  if (document.getElementById("feedback-box")) return;
  const box = document.createElement("div");
  box.id = "feedback-box";
  box.innerHTML = `<form class="card"><h2>Feedback</h2><p class="muted">What works, what doesn't, what's confusing, or what you'd like. It goes to the bot's owner with your name.</p>
    <textarea name="text" rows="5" maxlength="2000" required></textarea><div class="controls"><button>Send</button><button type="button" id="feedback-cancel">Cancel</button></div></form>`;
  document.body.append(box);
  const form = box.querySelector("form")!;
  (form.elements.namedItem("text") as HTMLTextAreaElement).focus();
  form.onsubmit = (e) => {
    e.preventDefault();
    send("feedback", (form.elements.namedItem("text") as HTMLTextAreaElement).value);
    box.remove();
  };
  box.querySelector<HTMLButtonElement>("#feedback-cancel")!.onclick = () => box.remove();
}

function toast(text: string) {
  const note = document.createElement("div");
  note.className = "toast";
  note.textContent = text.replace(/\*\*|`|\[|\]\([^)]*\)/g, "");
  document.body.append(note);
  setTimeout(() => note.remove(), 3500);
}

function fail(error: unknown) {
  app.innerHTML = `<section class="card"><h1>Couldn't connect</h1><p class="muted">${esc(String(error instanceof Error ? error.message : error))}</p></section>`;
}

connect().catch(fail);
