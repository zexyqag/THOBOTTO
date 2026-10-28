import { DiscordSDK } from "@discord/embedded-app-sdk";
import "./style.css";

import * as captions from "./captions";
import { askFeedback } from "./feedback";
import * as music from "./music";
import * as poll from "./poll";
import * as quiz from "./quiz";
import * as sounds from "./sounds";
import { api, app, esc, inDiscord, params, toast } from "./ui";
import * as watch from "./watch";
import * as wrapped from "./wrapped";

export type Song = { title: string; author: string; uri?: string; art?: string; length?: number; by?: string; added: boolean; canRemove?: boolean };
export type Watched = { problem?: string; tunedTo?: string; title: string; uploader?: string; page: string; seconds?: number; stream?: string; paused: boolean; position: number; queue: { canRemove?: boolean; title: string; seconds?: number }[] };
export type State = {
  channel: string; helper?: string; paused?: boolean; canControl?: boolean;
  track?: Song; position?: number; queue?: Song[]; lyrics?: { text: string; lines: { at: number; text: string }[] };
  on?: { music: boolean; watch: boolean; quiz: boolean; sounds: boolean; wrapped: boolean };
  watch?: Watched; watchElsewhere?: { channel: string; name: string; title: string }[]; captions?: Captions; quiz?: Quiz; poll?: Poll; skipVotes?: { votes: number; needed: number };
};
export type Poll = { question: string; options: { text: string; votes: number }[]; mine?: number; closed: boolean; left: number; asker: boolean };
export type Quiz = {
  phase: "starting" | "asking" | "reveal" | "over"; round: number; rounds: number; left: number; choices: string[]; mine?: number;
  answered: number; players: number; correct?: number; song?: string; art?: string; right: { name: string; gain: number }[]; gain?: number;
  scores: { name: string; score: number; me: boolean }[]; canStop: boolean;
};
export type Captions = { listening: boolean; heard: boolean; canQuote: boolean; lines: { id: number; name: string; text: string; at: number; quotable: boolean }[] };

// What a tab draws from: the root to draw in, the last state and when it came (what moves on its own is moved on
// from there), and the way back: an action for the hub, and a redraw after a change of the tab's own.
export type Live = { root: HTMLElement; state: State | null; receivedAt: number; send(action: string, value?: unknown): void; redraw(): void };

// Which tab shows: Watch when a video is on (until someone picks another).
type Tab = "music" | "watch" | "quiz" | "poll" | "sounds" | "wrapped" | "captions";
let tab: Tab | null = null;
const tabNames: Record<Tab, string> = { music: "🎵 Music", watch: "🎬 Watch", quiz: "🎯 Quiz", poll: "📊 Poll", sounds: "🔊 Sounds", wrapped: "✨ Wrapped", captions: "💬 Captions" };

const live: Live = { root: app, state: null, receivedAt: 0, send, redraw: render };
let socket: WebSocket | null = null;
let failures = 0;

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
      live.state = null;
      watch.leave();
      socket?.close();
    }
  }, 10000);

async function connect() {
  const { session, guild, channel } = await signIn();
  const scheme = location.protocol === "https:" ? "wss" : "ws";
  socket = new WebSocket(`${scheme}://${location.host}${api}/live?session=${encodeURIComponent(session)}&guild=${guild}&channel=${channel}`);
  socket.onopen = () => {
    failures = 0;
    wrapped.reconnected();
    sounds.reconnected();
  };
  socket.onmessage = (event) => {
    const message = JSON.parse(event.data);
    if (message.reply) {
      if (wrapped.failed(message.reply)) render();
      return toast(message.reply);
    }
    if (message.wrapped) { wrapped.show(message.wrapped); return render(); }
    if (message.sounds) { sounds.show(message.sounds); return render(); }
    live.state = message;
    live.receivedAt = performance.now();
    render();
  };
  // Lost (a deploy, a network hiccup): try again shortly.
  socket.onclose = () => {
    // Never got through: say why it may be, and keep trying.
    if (++failures >= 3 && !live.state)
      app.innerHTML = `<section class="card"><h1>Can't reach the bot live</h1><p class="muted">Signed in, but the live connection doesn't get through. If the bot is behind a reverse proxy, turn on WebSocket support for its address (in Nginx Proxy Manager: the proxy host → Websockets Support). Still trying…</p></section>`;
    setTimeout(() => connect().catch(fail), 3000);
  };
}

function send(action: string, value?: unknown) {
  socket?.send(JSON.stringify({ do: action, value }));
}

// What's typed (and where the cursor is) stays through redrawing on each live update.
function render() {
  if (!live.state) return;
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

const shown = (t: Tab) => t === "captions" ? !!live.state!.captions : t === "poll" || live.state!.on?.[t] !== false;

// The tab bar, then the tab that's on draws the page; the video player is let go whenever another tab is.
function draw() {
  const state = live.state;
  if (!state) return;
  app.classList.remove("fit");
  if (tab && !shown(tab)) tab = null;
  const shownTabs = (Object.keys(tabNames) as Tab[]).filter(shown);
  const current = tab ?? (state.quiz ? "quiz" : state.watch ? "watch" : shown("music") ? "music" : shownTabs[0]);
  const tabs = `<nav class="tabs">${shownTabs.map((t) => `<button data-tab="${t}" class="${t === current ? "here" : ""}">${tabNames[t]}</button>`).join("")}<button id="feedback" class="pop" title="Tell the bot's owner what works, what doesn't, or what you'd like">💬 Feedback</button>${inDiscord && panel ? `<button id="pop" class="pop" title="The same page in your browser, without Discord around it">↗ Open in browser</button>` : ""}</nav>`;
  if (current === "watch") watch.render(live, tabs);
  else {
    watch.leave();
    if (current === "captions") captions.render(live, tabs);
    else if (current === "quiz") quiz.render(live, tabs);
    else if (current === "wrapped") wrapped.render(live, tabs);
    else if (current === "poll") poll.render(live, tabs);
    else if (current === "sounds") sounds.render(live, tabs);
    else music.render(live, tabs);
  }
  wireTabs();
}

function wireTabs() {
  document.querySelectorAll<HTMLButtonElement>("button[data-tab]").forEach((b) => (b.onclick = () => { tab = b.dataset.tab as Tab; render(); }));
  const feedback = document.getElementById("feedback");
  if (feedback) feedback.onclick = () => askFeedback(send);
  const pop = document.getElementById("pop");
  if (pop) pop.onclick = () => sdk?.commands.openExternalLink({ url: `${panel}/activity/` }).catch(() => toast(`Open ${panel}/activity/ in your browser.`));
}

function fail(error: unknown) {
  app.innerHTML = `<section class="card"><h1>Couldn't connect</h1><p class="muted">${esc(String(error instanceof Error ? error.message : error))}</p></section>`;
}

connect().catch(fail);
