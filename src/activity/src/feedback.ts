import type { Live } from "./main";

// A box over the page (outside what's redrawn), so typing isn't interrupted.
export function askFeedback(send: Live["send"]) {
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
