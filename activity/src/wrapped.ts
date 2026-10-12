import type { Live } from "./main";
import { plain } from "./ui";

// Wrapped as stories: one slide at a time, on by itself; tap the right side for the next, the left to go back.
type Card = { title: string; intro?: string; color?: number; facts: { label: string; value: string }[]; lists: { heading: string; lines: string[] }[] };
type Wrapped = { off?: string; who: "me" | "server"; period: string; periods: string[]; cards: Card[] };
type Slide = { color?: number; html: string };
const periodNames: Record<string, string> = { ThisMonth: "This month", LastMonth: "Last month", ThisYear: "This year", LastYear: "Last year", AllTime: "All time" };
const slideMs = 6000;
let live: Live | null = null;
let wrapped: Wrapped | null = null;
let asked = false;
let loading = false;
let slides: Slide[] = [];
let slide = 0;
let slideAt = 0;
let storyPaused = false;
let seq = 0;
let shownStory = "";

function ask(who: string, period?: string) {
  asked = true;
  loading = true;
  shownStory = "";
  live!.send("wrapped", { who, period });
  live!.redraw();
}

// The Wrapped the hub sent back, as slides: a card's title, then its facts two at a time, then each list.
export function show(w: Wrapped) {
  wrapped = w;
  loading = false;
  seq++;
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
}

// Asked before a reconnect: ask again.
export function reconnected() {
  if (loading) asked = false;
}

// An action that failed while a Wrapped was being fetched: stop waiting for it. True when the tab needs drawing again.
export function failed(reply: string): boolean {
  if (!loading || !reply.startsWith("That didn't work")) return false;
  loading = false;
  shownStory = "";
  return true;
}

export function render(l: Live, tabs: string) {
  live = l;
  if (!asked) ask("me");
  // The live updates don't touch the story, so its slide isn't drawn again.
  const key = `${tabs}|${seq}|${slide}|${storyPaused}|${loading}`;
  if (key === shownStory && document.querySelector(".story, .card.wide p.muted")) return;
  shownStory = key;
  const w = wrapped;
  const pick = w && !w.off ? `<p class="muted">Wrapped as stories, to watch together in the call: yours or the server's, a slide at a time. Every period and helper voice, to browse or share, is on the panel's Wrapped page.</p>
      <div class="controls">
      <button data-who="me" class="${w.who === "me" ? "here" : ""}">You</button><button data-who="server" class="${w.who === "server" ? "here" : ""}">The server</button>
      <select id="period">${w.periods.map((p) => `<option value="${p}" ${p === w.period ? "selected" : ""}>${periodNames[p] ?? p}</option>`).join("")}</select>
      <button id="story-pause">${storyPaused ? "▶️" : "⏸️"}</button></div>` : "";
  const s = slides[slide];
  const hue = (n: number) => `#${n.toString(16).padStart(6, "0")}`;
  const ground = s?.color ? `linear-gradient(160deg, ${hue(s.color)}, #1e1f22 85%)` : `linear-gradient(160deg, hsl(${(slide * 47) % 360} 55% 35%), #1e1f22 85%)`;
  const story = !w || loading ? `<p class="muted">Getting the stories…</p>`
    : w.off ? `<p class="muted">${plain(w.off)}</p>`
    : !s ? `<p class="muted">No stories for then yet.</p>`
    : `<div class="story" style="background: ${ground}">
        <div class="segments">${slides.map((_, i) => `<span><i id="seg-${i}" style="width: ${i < slide ? 100 : 0}%"></i></span>`).join("")}</div>
        <div class="slide">${s.html}</div>
        <button class="back" aria-label="Back"></button><button class="next" aria-label="Next"></button>
      </div>`;
  l.root.innerHTML = `${tabs}<section class="card wide">${pick}${story}</section>`;
  document.querySelectorAll<HTMLButtonElement>("button[data-who]").forEach((b) => (b.onclick = () => ask(b.dataset.who!, wrapped?.period)));
  const period = document.getElementById("period") as HTMLSelectElement | null;
  if (period) period.onchange = () => ask(wrapped!.who, period.value);
  const pause = document.getElementById("story-pause");
  if (pause) pause.onclick = () => { storyPaused = !storyPaused; slideAt = performance.now() - storyAt; l.redraw(); };
  const go = (step: number) => { slide = Math.max(0, Math.min(slides.length - 1, slide + step)); slideAt = performance.now(); l.redraw(); };
  document.querySelector<HTMLButtonElement>(".story .back")?.addEventListener("click", () => go(-1));
  document.querySelector<HTMLButtonElement>(".story .next")?.addEventListener("click", () => go(1));
}

// How far into the slide (ms), and on to the next when it's time.
let storyAt = 0;
function tick() {
  if (!slides.length || !document.querySelector(".story")) return;
  storyAt = storyPaused ? storyAt : performance.now() - slideAt;
  const bar = document.getElementById(`seg-${slide}`);
  if (bar) bar.style.width = `${Math.min(100, (storyAt / slideMs) * 100)}%`;
  if (!storyPaused && storyAt >= slideMs && slide < slides.length - 1) {
    slide++;
    slideAt = performance.now();
    live?.redraw();
  }
}
setInterval(tick, 100);
