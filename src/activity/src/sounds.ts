import type { Live } from "./main";
import { esc, plain } from "./ui";

type Sounds = { off?: string; list?: { name: string; seconds: number }[] };

let live: Live | null = null;
let soundList: Sounds | null = null;
let asked = false;
let filter = "";

// The list the hub sent back.
export function show(sounds: Sounds) {
  soundList = sounds;
}

// Asked before a reconnect: ask again.
export function reconnected() {
  if (!soundList) asked = false;
}

// The server's soundboard: tap one and a helper plays it in the channel.
export function render(l: Live, tabs: string) {
  live = l;
  if (!asked) { asked = true; l.send("sounds"); }
  if (document.activeElement?.id === "sound-filter" && document.querySelector(".soundgrid")) return draw();
  const body = !soundList ? `<p class="muted">Getting the sounds…</p>`
    : soundList.off ? `<p class="muted">${plain(soundList.off)}</p>`
    : `<input type="text" id="sound-filter" placeholder="Find a sound" value="${esc(filter)}" /><div class="soundgrid"></div>`;
  l.root.innerHTML = `${tabs}<section class="card wide"><div class="quiz-head"><h2>Soundboard</h2><button id="sounds-refresh">↻</button></div>${body}</section>`;
  const field = document.getElementById("sound-filter") as HTMLInputElement | null;
  if (field) field.oninput = () => { filter = field.value; draw(); };
  document.getElementById("sounds-refresh")!.onclick = () => l.send("sounds");
  draw();
}

function draw() {
  const grid = document.querySelector(".soundgrid");
  if (!grid || !soundList?.list) return;
  const shown = soundList.list.filter((s) => s.name.toLowerCase().includes(filter.toLowerCase()));
  grid.innerHTML = shown.length === 0 ? `<p class="muted">${soundList.list.length === 0 ? "No sounds yet: they're added on the panel's Sounds page or with /sound." : "None match."}</p>`
    : shown.map((s) => `<button data-sound="${esc(s.name)}">🔊 ${esc(s.name)}<span class="muted">${s.seconds}s</span></button>`).join("");
  grid.querySelectorAll<HTMLButtonElement>("button[data-sound]").forEach((b) => (b.onclick = () => live?.send("sound-play", b.dataset.sound)));
}
