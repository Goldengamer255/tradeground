// The playground: every game on one page, easiest first, each with what it teaches,
// how long it takes, how hard it is, and a "How to play" guide.

import * as api from "./api.js";
import { howToButton } from "./howto.js";
import { esc, saveSeat, setTopbar } from "./ui.js";

// Small drawings, one per game. Decorative only (aria-hidden); colours come from CSS classes.
const ART = {
  "up-or-down": `
    <svg viewBox="0 0 120 80" aria-hidden="true">
      <line class="art-wick up" x1="14" x2="14" y1="44" y2="66"></line><rect class="art-body up" x="9" y="50" width="10" height="12"></rect>
      <line class="art-wick down" x1="32" x2="32" y1="40" y2="62"></line><rect class="art-body down" x="27" y="46" width="10" height="10"></rect>
      <line class="art-wick up" x1="50" x2="50" y1="26" y2="54"></line><rect class="art-body up" x="45" y="32" width="10" height="16"></rect>
      <line class="art-wick up" x1="68" x2="68" y1="16" y2="40"></line><rect class="art-body up" x="63" y="20" width="10" height="14"></rect>
      <line class="art-now" x1="82" x2="82" y1="6" y2="74"></line>
      <text class="art-q" x="100" y="48" text-anchor="middle">?</text>
    </svg>`,
  "trade-history": `
    <svg viewBox="0 0 120 80" aria-hidden="true">
      <polyline class="art-line" points="6,60 20,52 32,58 46,38 58,44 72,24 86,30 100,14 114,20"></polyline>
      <circle class="art-buy" cx="32" cy="58" r="5"></circle>
      <circle class="art-sell" cx="100" cy="14" r="5"></circle>
    </svg>`,
  "market-maker": `
    <svg viewBox="0 0 120 80" aria-hidden="true">
      <rect class="art-die" x="14" y="18" width="40" height="40" rx="9"></rect>
      <circle class="art-pip" cx="26" cy="30" r="4"></circle><circle class="art-pip" cx="42" cy="46" r="4"></circle>
      <rect class="art-die hidden-die" x="66" y="26" width="40" height="40" rx="9"></rect>
      <text class="art-q" x="86" y="54" text-anchor="middle">?</text>
    </svg>`,
};

const LEVELS = { Easy: 1, Medium: 2, Advanced: 3 };

const level = (name) => `
  <span class="level-meter" aria-label="Difficulty: ${name}">
    ${[1, 2, 3].map((i) => `<span class="bar${i <= LEVELS[name] ? " on" : ""}"></span>`).join("")}
    <span class="level-name">${name}</span>
  </span>`;

function gameRow({ id, name, level: difficulty, time, teaches, body, actions, soon = false }) {
  return `
    <article class="game${soon ? " soon" : ""}" aria-labelledby="game-${id}">
      <div class="game-art">${ART[id]}</div>
      <div class="game-text">
        <h2 id="game-${id}">${esc(name)}${soon ? ' <span class="soon-tag">Coming soon</span>' : ""}</h2>
        <p class="game-meta">${level(difficulty)}<span>${esc(time)}</span></p>
        <p class="game-teaches">Teaches: ${esc(teaches)}</p>
        <p class="game-body">${body}</p>
      </div>
      <div class="game-actions">${actions}</div>
    </article>`;
}

export async function renderHome(root) {
  setTopbar("");

  root.innerHTML = `
    <div class="home">
      <section class="home-intro">
        <h1>Learn to trade by playing.</h1>
        <p>Tradeground is a playground for trading. Real charts, pretend money, and three games
          that go from your first chart to how professional traders think. No sign-up, nothing to lose.</p>
      </section>

      <section class="games" aria-label="Games">
        ${gameRow({
          id: "up-or-down",
          name: "Up or Down",
          level: "Easy",
          time: "About 1 minute a chart, on your own",
          teaches: "reading price charts",
          body: "A real crypto chart from the past, stopped at \"now\". Zoom in and out from 1-minute to 4-hour candles, guess whether the price goes up or down next, then watch what actually happened.",
          actions: `<a class="button" href="#/chart">Play</a>${howToButton("up-or-down")}`,
        })}
        ${gameRow({
          id: "trade-history",
          name: "Trade Through History",
          level: "Medium",
          time: "About 5 minutes, on your own",
          teaches: "buying, selling and limiting your losses, like a real trading app",
          body: "Start with $10,000 of pretend money in a real moment from market history. Buy, sell and set a stop-loss as the chart plays, then see if you beat simply buying and holding.",
          actions: "",
          soon: true,
        })}
        ${gameRow({
          id: "market-maker",
          name: "Market Maker",
          level: "Advanced",
          time: "10 to 15 minutes, 2 or more players",
          teaches: "how trading firms make money",
          body: "Everyone trades something worth the total of hidden dice. You see one die, the host reveals the rest as news, and you buy low and sell high against the other players.",
          actions: `<div class="host-buttons" id="host-buttons"><span class="empty">Loading…</span></div>${howToButton("market-maker")}`,
        })}
      </section>

      <form class="join-form" id="join-form" novalidate>
        <div>
          <h2><label for="code">Got a room code?</label></h2>
          <p class="empty" id="join-help">For Market Maker: your host shows the code on their screen.</p>
        </div>
        <div class="join-row">
          <input class="code-input" id="code" name="code" autocomplete="off" autocapitalize="characters"
            spellcheck="false" maxlength="6" placeholder="ABC123" aria-describedby="join-help join-error" required>
          <button class="button" type="submit">Join</button>
        </div>
        <p class="ticket-result rejected" id="join-error" role="alert"></p>
      </form>

      <p class="home-footnote">No sign-up, no email, no real names. Players get a random name like
        Calm Otter 81. Educational only: no real money and not financial advice.</p>
    </div>`;

  wireJoin(root);
  await loadHostButtons(root);
  return null;
}

function wireJoin(root) {
  const form = root.querySelector("#join-form");
  const input = root.querySelector("#code");
  const error = root.querySelector("#join-error");

  input.addEventListener("input", () => {
    input.value = input.value.toUpperCase().replace(/[^A-Z0-9]/g, "");
    error.textContent = "";
  });

  form.addEventListener("submit", async (event) => {
    event.preventDefault();
    const code = input.value.trim();
    if (code.length !== 6) {
      error.textContent = "Room codes have 6 letters and numbers.";
      input.focus();
      return;
    }

    const joined = await api.joinRoom(code);
    if (!joined.ok) {
      error.textContent = joined.error;
      input.focus();
      return;
    }

    saveSeat(code, { role: "player", key: joined.data.traderKey });
    location.hash = `#/play/${code}`;
  });
}

/** Market Maker has two versions (game modes); each gets its own "Host" button. */
async function loadHostButtons(root) {
  const holder = root.querySelector("#host-buttons");
  const modes = await api.getModes();
  if (!modes.ok) {
    holder.innerHTML = `<p class="ticket-result rejected">${esc(modes.error)}</p>`;
    return;
  }

  holder.innerHTML = modes.data
    .sort((a, b) => (a.audience === b.audience ? 0 : a.audience === "Explorer" ? -1 : 1))
    .map((m) => `<button class="button" type="button" data-mode="${esc(m.id)}"
        title="${esc(m.description)}">Host ${esc(m.name)}${m.audience === "Explorer" ? " (kids)" : ""}</button>`)
    .join("");

  holder.addEventListener("click", async (event) => {
    const button = event.target.closest("[data-mode]");
    if (!button) return;
    button.disabled = true;
    const created = await api.createRoom(button.dataset.mode);
    button.disabled = false;
    if (!created.ok) {
      button.insertAdjacentHTML("afterend", `<p class="ticket-result rejected">${esc(created.error)}</p>`);
      return;
    }

    saveSeat(created.data.code, { role: "host", key: created.data.hostKey });
    location.hash = `#/host/${created.data.code}`;
  });
}
