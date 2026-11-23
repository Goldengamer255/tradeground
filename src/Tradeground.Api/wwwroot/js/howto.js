// "How to play" guides, one per game. Written for someone who has never traded.
// Each guide opens in a <dialog>: the browser handles focus, Escape and the backdrop for us.

import { esc } from "./ui.js";

export const GUIDES = {
  "up-or-down": {
    title: "How to play Up or Down",
    intro: "You see a real price chart from the past, stopped at \"now\". Guess where the price goes next.",
    steps: [
      "Pick a timeframe. On \"15m\", each candle is 15 minutes of trading; on \"1w\", each is a week.",
      "Read the chart. Each candle is one slice of time: blue means the price went up during it, orange means it went down. The thin line shows the highest and lowest price in that slice.",
      "Press Up if you think the price will be higher 20 candles from now, or Down if you think it will be lower.",
      "Watch the next 20 candles play out. Then you find out if you were right, and which market and dates it was.",
    ],
    note: "Nobody gets this right every time, including professionals. Pay attention to how often it feels like a coin flip: that is a real lesson about markets.",
  },
  "market-maker": {
    title: "How to play Market Maker",
    intro: "Picture an airport currency booth. It buys dollars for 1.60 and sells them for 1.70, and earns the gap in between. In this game, you are the booth.",
    steps: [
      "Everyone trades the same thing, and its true value is hidden: it is the total of some dice (in Lemonade Stand, the lemons in some crates).",
      "You secretly see one die. The others average 3.5 each, so you can estimate the total. The \"fair value\" number on your screen does that sum for you.",
      "Buy when someone will sell for less than fair value; sell when someone will pay more. Put your own prices on the order book, or click someone else's price to trade with them.",
      "The host reveals dice as \"news\" during the round. Your estimate gets better, so change your prices to match.",
      "When the round ends, all the dice are shown. Everything you hold is worth the true total, and the player with the most profit wins.",
    ],
    note: "You need at least two players: one person hosts on a big screen, everyone else joins with the room code. Computer traders, so you can play alone, are coming next.",
  },
};

let dialog;

function ensureDialog() {
  if (dialog) return dialog;
  dialog = document.createElement("dialog");
  dialog.className = "howto";
  dialog.setAttribute("aria-labelledby", "howto-title");
  document.body.append(dialog);

  // Clicking the dimmed backdrop (outside the box) closes it, like most dialogs people know.
  dialog.addEventListener("click", (event) => {
    if (event.target === dialog) dialog.close();
  });
  return dialog;
}

export function showHowTo(gameId) {
  const guide = GUIDES[gameId];
  if (!guide) return;
  const d = ensureDialog();
  d.innerHTML = `
    <div class="howto-body">
      <h2 id="howto-title">${esc(guide.title)}</h2>
      <p class="howto-intro">${esc(guide.intro)}</p>
      <ol class="howto-steps">${guide.steps.map((s) => `<li>${esc(s)}</li>`).join("")}</ol>
      <p class="howto-note">${esc(guide.note)}</p>
      <form method="dialog"><button class="button" type="submit" autofocus>Got it</button></form>
    </div>`;
  d.showModal();
}

// First-time players see the guide automatically, once per browser.
const seenKey = (gameId) => `tradeground.seen.${gameId}`;

export function showHowToFirstTime(gameId) {
  try {
    if (localStorage.getItem(seenKey(gameId))) return;
    localStorage.setItem(seenKey(gameId), "1");
  } catch {
    return; // Storage blocked: don't nag on every visit; the button is always there.
  }
  showHowTo(gameId);
}

/** A small "How to play" button to put inside a game's header. */
export const howToButton = (gameId) =>
  `<button class="button quiet howto-open" type="button" data-howto="${esc(gameId)}">How to play</button>`;

// One listener for every "How to play" button on any page.
document.addEventListener("click", (event) => {
  const button = event.target.closest?.("[data-howto]");
  if (button) showHowTo(button.dataset.howto);
});
