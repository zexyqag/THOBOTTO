import type { Live, Song } from "./main";
import { art, esc, time } from "./ui";

let live: Live | null = null;
let shownTrack = "";

// Where the song is now: the last position heard, moved on by the time since (unless paused).
function position(): number {
  const state = live?.state;
  if (!state?.track) return 0;
  const moved = state.paused ? 0 : performance.now() - live!.receivedAt;
  return Math.min((state.position ?? 0) + moved, state.track.length ?? Infinity);
}

// The song playing: its cover, where it is, the controls, the lyrics and what's up next; karaoke when asked.
export function render(l: Live, tabs: string) {
  live = l;
  const { root, state } = l;
  if (karaoke && state!.track) return renderKaraoke(tabs);
  const addForm = `<form id="add"><input type="text" name="q" placeholder="Add a song: search words or a link" maxlength="300" /><button>Add</button></form>`;
  if (!state!.track) {
    root.innerHTML = `${tabs}<section class="card empty"><h1>Nothing's playing in ${esc(state!.channel)}</h1><p class="muted">Add a song and a helper brings it.</p>${addForm}</section>`;
    wire();
    return;
  }
  const t = state!.track;
  const key = `${t.title}\n${t.author}`;
  const lines = state!.lyrics?.lines ?? [];
  const lyrics = !state!.lyrics ? `<p class="muted">No lyrics found.</p>`
    : lines.length > 0 ? lines.map((l, i) => `<p data-i="${i}">${esc(l.text) || "♪"}</p>`).join("")
    : `<pre>${esc(state!.lyrics.text)}</pre>`;
  const queue = state!.queue ?? [];
  const added = queue.filter((s) => s.added);
  const later = queue.filter((s) => !s.added);
  const list = (songs: Song[], start: number) => `<ol start="${start}">${songs.map((s, i) => `<li>${esc(s.title)} <span class="muted">· ${esc(s.author)}${s.by ? ` · ${esc(s.by)}` : ""}</span>${s.votes != null ? ` <button class="link${s.voted ? " voted" : ""}" data-vote="${start + i}" title="${s.voted ? "Take your vote back" : "Vote for it"}">👍${s.votes > 0 ? ` ${s.votes}` : ""}</button>` : ""}${s.canRemove ? ` <button class="link" data-remove="${start + i}" title="Take it out of the queue">✕</button>` : ""}</li>`).join("")}</ol>`;
  // The lyrics keep their scroll when only the rest changes.
  const scroll = shownTrack === key ? document.querySelector(".lyrics")?.scrollTop ?? 0 : 0;
  root.innerHTML = `${tabs}
    <section class="card now">
      ${t.art ? `<img src="${art(t.art)}" alt="" />` : `<div class="noart"></div>`}
      <div class="info">
        <h1>${esc(t.title)}</h1>
        <p class="author">${esc(t.author)}</p>
        <p class="muted">${t.by ? `Asked for by ${esc(t.by)} · ` : ""}${esc(state!.helper)} in ${esc(state!.channel)}</p>
        <div class="bar"><div id="bar"></div></div>
        <div class="times muted"><span id="at"></span><span>${t.length ? time(t.length) : "live"}</span></div>
        ${state!.canControl ? `<div class="controls">
          <button data-do="pause">${state!.paused ? "▶️ Play" : "⏸️ Pause"}</button>
          <button data-do="skip">⏭️ Skip</button>
          <button data-do="stop">⏹️ Stop</button>
          <button id="karaoke">🎤 Karaoke</button>
        </div>` : `<div class="controls"><button data-do="skip">${state!.canSkip ? "⏭️ Skip" : "🗳️ Vote to skip"}</button><button id="karaoke">🎤 Karaoke</button></div>`}
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
  root.classList.add("fit");
  // Back where it was at once: the smooth scrolling (for following the song) would slide it from the top.
  const box = document.querySelector<HTMLElement>(".lyrics");
  if (box) {
    box.style.scrollBehavior = "auto";
    box.scrollTop = scroll;
    box.style.scrollBehavior = "";
  }
  wire();
  tick();
}

// How many voted to skip, of how many it takes.
function skipBar() {
  const s = live?.state?.skipVotes;
  if (!s || s.votes === 0) return "";
  return `<div class="votes"><div class="bar"><div style="width: ${Math.min(100, (s.votes / s.needed) * 100)}%"></div></div><span class="muted">🗳️ ${s.votes} of ${s.needed} to skip</span></div>`;
}

// Karaoke: the line being sung large, the next ones after it.
let karaoke = false;
let shownKaraoke = "";
function renderKaraoke(tabs: string) {
  const state = live!.state!;
  // Drawn again only for another song (or tabs), so the line in view stays put.
  const key = `${tabs}|${state.track!.title}|${state.lyrics?.lines.length ?? -1}`;
  if (key === shownKaraoke && document.querySelector(".karaoke")) return;
  shownKaraoke = key;
  const synced = (state.lyrics?.lines.length ?? 0) > 0;
  live!.root.innerHTML = `${tabs}<section class="card wide karaoke">
      <div class="karaoke-head"><span class="muted">${esc(state.track!.title)} · ${esc(state.track!.author)}</span><button id="karaoke">✖️ Leave karaoke</button></div>
      ${synced ? `<p class="before" id="k-before"></p><p class="sing" id="k-now"></p><p class="after" id="k-next"></p><p class="after" id="k-later"></p>`
        : `<p class="sing">${state.lyrics ? "These lyrics aren't timed, so they can't follow along." : "No lyrics found for this one."}</p>`}
      <div class="bar"><div id="bar"></div></div>
    </section>`;
  lastKaraoke = -2;
  wire();
  tick();
}
let lastKaraoke = -2;

function wire() {
  const sing = document.getElementById("karaoke");
  if (sing) sing.onclick = () => { karaoke = !karaoke; live?.redraw(); };
  document.querySelectorAll<HTMLButtonElement>("button[data-do]").forEach((b) => (b.onclick = () => live?.send(b.dataset.do!)));
  document.querySelectorAll<HTMLButtonElement>("button[data-remove]").forEach((b) => (b.onclick = () => live?.send("remove", Number(b.dataset.remove))));
  document.querySelectorAll<HTMLButtonElement>("button[data-vote]").forEach((b) => (b.onclick = () => live?.send("vote", Number(b.dataset.vote))));
  const add = document.getElementById("add") as HTMLFormElement | null;
  if (add) add.onsubmit = (e) => {
    e.preventDefault();
    const q = (add.elements.namedItem("q") as HTMLInputElement).value.trim();
    if (q) live?.send("add", q);
    add.reset();
  };
}

// The bar, the time and the lyric line that's being sung, a few times a second.
let lastLine = -1;
function tick() {
  const state = live?.state;
  if (!state?.track) return;
  const at = position();
  const bar = document.getElementById("bar");
  if (bar && state.track.length) bar.style.width = `${(at / state.track.length) * 100}%`;
  const shown = document.getElementById("at");
  if (shown) shown.textContent = time(at);
  const lines = state.lyrics?.lines ?? [];
  let current = -1;
  for (const [i, line] of lines.entries()) if (line.at <= at) current = i;
  const now = document.getElementById("k-now");
  if (now && current !== lastKaraoke) {
    const words = lines.map((l) => l.text || "♪");
    document.getElementById("k-before")!.textContent = words[current - 1] ?? "";
    now.textContent = words[current] ?? "♪";
    document.getElementById("k-next")!.textContent = words[current + 1] ?? "";
    document.getElementById("k-later")!.textContent = words[current + 2] ?? "";
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
