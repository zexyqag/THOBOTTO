import type { Live } from "./main";
import { esc } from "./ui";

// Caption lines picked for a quote.
const picked = new Set<number>();
let live: Live | null = null;

// What's said in the call, as the helpers hear it; lines tapped become a quote.
export function render(l: Live, tabs: string) {
  live = l;
  const { root, state } = l;
  const c = state!.captions;
  if (!c) {
    root.innerHTML = `${tabs}<section class="card empty"><h1>Captions are for those in ${esc(state!.channel)}</h1><p class="muted">Join the voice channel to see them.</p></section>`;
    return;
  }
  const kept = new Set(c.lines.map((line) => line.id));
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
    : c.lines.map((line) => `<p data-line="${line.id}" class="${picked.has(line.id) ? "picked" : ""} ${line.quotable ? "" : "shy"}" title="${line.quotable ? "" : "Rather not be quoted"}"><span class="when muted">${new Date(line.at).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}</span> <b>${esc(line.name)}</b> ${esc(line.text)}</p>`).join("");
  const quoting = c.canQuote ? `<div class="controls"><button id="save-quote" ${picked.size === 0 ? "disabled" : ""}>💬 Save quote${picked.size > 0 ? ` (${picked.size})` : ""}</button>${picked.size > 0 ? `<button id="clear-quote">✖️ Clear</button>` : ""}<span class="muted">Tap lines to pick them.</span></div>` : "";
  root.innerHTML = `${tabs}<section class="card wide"><h2>Captions in ${esc(state!.channel)}</h2>${notes}<div class="captions">${lines}</div>${quoting}</section>`;
  const shown = document.querySelector(".captions")!;
  shown.scrollTop = atEnd ? shown.scrollHeight : scroll;
  if (c.canQuote)
    shown.querySelectorAll<HTMLElement>("p[data-line]:not(.shy)").forEach((p) => (p.onclick = () => {
      const id = Number(p.dataset.line);
      if (!picked.delete(id)) picked.add(id);
      l.redraw();
    }));
  const save = document.getElementById("save-quote");
  if (save) save.onclick = () => { l.send("quote", [...picked]); picked.clear(); l.redraw(); };
  const clear = document.getElementById("clear-quote");
  if (clear) clear.onclick = () => { picked.clear(); l.redraw(); };
  document.querySelectorAll<HTMLButtonElement>("button[data-do]").forEach((b) => (b.onclick = () => live?.send(b.dataset.do!)));
}
