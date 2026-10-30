import type { Live, Quiz } from "./main";
import { art, esc } from "./ui";

let live: Live | null = null;

// The music quiz: a clip plays, everyone picks its name from four; quicker right answers score more.
export function render(l: Live, tabs: string) {
  live = l;
  const { root, state } = l;
  const q = state!.quiz;
  const inChannel = !!state!.captions;
  const scores = (list: Quiz["scores"]) => list.length === 0 ? "" : `<ol class="scores">${list.map((s, i) => `<li class="${s.me ? "me" : ""}"><span class="place">${s.score > 0 ? ["🥇", "🥈", "🥉"][i] ?? `${i + 1}.` : `${i + 1}.`}</span> ${esc(s.name)} <b>${s.score}</b></li>`).join("")}</ol>`;
  if (!q || q.phase === "over") {
    root.innerHTML = `${tabs}
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
    : revealed ? `<div class="reveal">${q.art ? `<img src="${art(q.art)}" alt="" />` : ""}<div>
        <h2>${esc(q.song)}</h2>
        <p>${q.gain ? `✅ +${q.gain}` : q.mine !== undefined && q.mine !== null ? "❌ Not this time" : "⏱️ No answer"}</p>
        <p class="muted">${q.right.length === 0 ? "Nobody got it." : q.right.map((r) => `${esc(r.name)} +${r.gain}`).join(" · ")}</p></div></div>` : "";
  root.innerHTML = `${tabs}
    <section class="card wide quiz"><div class="quiz-head"><h1>${head}</h1>${q.canStop ? `<button data-quiz="stop">⏹️ Stop</button>` : ""}</div>
      ${q.phase === "asking" ? `<div class="bar"><div id="quiz-bar"></div></div>` : ""}
      ${choices}${status}</section>
    ${q.scores.length > 0 ? `<section class="card wide"><h2>Scores</h2>${scores(q.scores)}</section>` : ""}`;
  quizTotal = q.phase === "asking" && quizRound !== q.round ? q.left : quizTotal;
  quizRound = q.phase === "asking" ? q.round : quizRound;
  document.querySelectorAll<HTMLButtonElement>("button[data-answer]").forEach((b) => (b.onclick = () => l.send("quiz-answer", Number(b.dataset.answer))));
  wire();
  tick();
}

function wire() {
  document.querySelectorAll<HTMLButtonElement>("button[data-quiz]").forEach((b) => (b.onclick = () => live?.send(`quiz-${b.dataset.quiz}`)));
}

// The time left in the round, between updates.
let quizTotal = 1;
let quizRound = 0;
function tick() {
  const q = live?.state?.quiz;
  if (!q) return;
  const left = Math.max(0, q.left - (performance.now() - live!.receivedAt));
  const shown = document.getElementById("quiz-left");
  if (shown) shown.textContent = String(Math.ceil(left / 1000));
  const bar = document.getElementById("quiz-bar");
  if (bar) bar.style.width = `${(left / quizTotal) * 100}%`;
}
setInterval(tick, 100);
