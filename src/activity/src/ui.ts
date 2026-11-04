// Inside Discord the page is proxied: our own addresses start with /.proxy. Outside (development, or a browser),
// it's the bot's /activity/ itself.
export const params = new URLSearchParams(location.search);
export const inDiscord = params.has("frame_id");
export const api = inDiscord ? "/.proxy/api" : "/activity/api";
export const app = document.getElementById("app")!;

export const time = (ms: number) => `${Math.floor(ms / 60000)}:${String(Math.floor(ms / 1000) % 60).padStart(2, "0")}`;
export const esc = (text: string | undefined) => (text ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]!);
// Discord's bold and list numbers don't show here.
export const plain = (text: string) => esc(text.replace(/\*\*|__|`/g, "").replace(/^\d+\.\s+/, ""));
// Cover art comes through the bot: an Activity may only load from the addresses Discord maps.
export const art = (url: string) => `${api}/art?url=${encodeURIComponent(url)}`;

export function toast(text: string) {
  const note = document.createElement("div");
  note.className = "toast";
  note.textContent = text.replace(/\*\*|`|\[|\]\([^)]*\)/g, "");
  document.body.append(note);
  setTimeout(() => note.remove(), 3500);
}

// Links marked data-open leave the Activity: inside Discord, Discord opens them (asking first); elsewhere a new tab.
let open: (url: string) => void = (url) => void window.open(url, "_blank", "noopener");
export const opensWith = (opener: (url: string) => void) => (open = opener);
document.addEventListener("click", (event) => {
  const link = (event.target as Element | null)?.closest?.("a[data-open]") as HTMLAnchorElement | null;
  if (!link) return;
  event.preventDefault();
  if (/^https?:\/\//.test(link.href)) open(link.href);
});
