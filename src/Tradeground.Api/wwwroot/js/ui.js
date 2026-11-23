// Small shared helpers: escaping, number formatting, the toast, the top bar, and seats.

const ESCAPES = { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" };

/** Escape anything that came from the server or the user before it goes into HTML. */
export const esc = (value) => String(value ?? "").replace(/[&<>"']/g, (c) => ESCAPES[c]);

const MINUS = "−";

export function fmt(n) {
  const abs = Math.abs(n);
  const text = Number.isInteger(abs)
    ? abs.toLocaleString("en-US")
    : abs.toLocaleString("en-US", { minimumFractionDigits: 1, maximumFractionDigits: 1 });
  return n < 0 ? MINUS + text : text;
}

export const signed = (n) => (n > 0 ? `+${fmt(n)}` : fmt(n));

export const trend = (n) => (n > 0 ? "up" : n < 0 ? "down" : "");

let toastTimer;
export function toast(message) {
  const el = document.getElementById("toast");
  el.textContent = message;
  el.classList.add("show");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.classList.remove("show"), 3500);
}

/**
 * The Content-Security-Policy blocks style="..." attributes in HTML (a common injection route),
 * so markup carries data-* values instead and this applies them through the DOM, which CSP allows.
 */
export function applyStyles(root) {
  root.querySelectorAll("[data-delay]").forEach((el) => el.style.setProperty("--delay", `${el.dataset.delay}ms`));
  root.querySelectorAll("[data-width]").forEach((el) => {
    el.style.width = `${el.dataset.width}%`;
  });
}

export function setTopbar(html) {
  document.getElementById("topbar-room").innerHTML = html;
}

export function notice(title, body, actions = '<a class="button" href="#/">Back to start</a>') {
  return `<section class="notice"><h1>${esc(title)}</h1><p>${esc(body)}</p><div>${actions}</div></section>`;
}

// Seats: which room this browser is in, and with which secret key.
// Player seats live in sessionStorage: one seat per TAB, so two players can share a computer,
// and a refresh keeps the seat. Host seats live in localStorage, so closing the host's tab by
// accident doesn't lose control of the room. Storage can be unavailable (private mode, blocked
// site data), so every access is wrapped and a seat falls back to living in memory.

const seatKey = (code) => `tradeground.seat.${code}`;
const memorySeats = new Map();

function read(storage, code) {
  try {
    return JSON.parse(storage().getItem(seatKey(code)) ?? "null");
  } catch {
    return null;
  }
}

function attempt(action) {
  try {
    action();
  } catch {
    // Storage blocked: the in-memory copy is all there is.
  }
}

const storageFor = (role) => (role === "host" ? () => localStorage : () => sessionStorage);

export function saveSeat(code, seat) {
  attempt(() => storageFor(seat.role)().setItem(seatKey(code), JSON.stringify(seat)));
  memorySeats.set(code, seat);
}

export function forgetSeat(code) {
  attempt(() => sessionStorage.removeItem(seatKey(code)));
  memorySeats.delete(code);
}

/** This tab's seat in a room: a player seat in this tab wins over a host seat in this browser. */
export const seatFor = (code) =>
  read(() => sessionStorage, code) ?? memorySeats.get(code) ?? read(() => localStorage, code);
