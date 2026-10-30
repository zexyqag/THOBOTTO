import type { Live } from "./main";
import { esc, time } from "./ui";

let live: Live | null = null;

// A poll for those in the channel: one question, a few answers, live bars.
export function render(l: Live, tabs: string) {
  live = l;
  const { root, state } = l;
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
  root.innerHTML = `${tabs}${poll}${form}`;
  const fields = [...document.querySelectorAll<HTMLInputElement | HTMLSelectElement>("#poll-new [name]")];
  if (fields.length === typed.length) fields.forEach((f, i) => (f.value = typed[i] ?? f.value));
  if (focused >= 0) fields[focused]?.focus();
  document.querySelectorAll<HTMLButtonElement>("button[data-vote]").forEach((b) => (b.onclick = () => l.send("poll-vote", Number(b.dataset.vote))));
  document.querySelector<HTMLButtonElement>("button[data-poll=close]")?.addEventListener("click", () => l.send("poll-close"));
  const create = document.getElementById("poll-new") as HTMLFormElement | null;
  if (create) create.onsubmit = (e) => {
    e.preventDefault();
    const data = new FormData(create);
    l.send("poll-create", { question: String(data.get("question")), options: data.getAll("option").map(String).filter((o) => o.trim()), minutes: Number(data.get("minutes")) });
    create.reset();
    (document.activeElement as HTMLElement | null)?.blur();
  };
  tick();
}

// The time left, between updates.
function tick() {
  const shown = document.getElementById("poll-left");
  const poll = live?.state?.poll;
  if (!shown || !poll) return;
  const left = Math.max(0, poll.left - (performance.now() - live!.receivedAt));
  shown.textContent = time(left);
}
setInterval(tick, 500);
