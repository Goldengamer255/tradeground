import * as api from "./api.js";
import { dieHtml } from "./dice.js";
import { applyStyles, esc, saveSeat, setTopbar } from "./ui.js";

// Ten face-down dice; three turn over when the page opens. The one moment of motion on this page.
const HERO = [undefined, 5, undefined, undefined, undefined, 2, undefined, undefined, 4, undefined];

export async function renderHome(root) {
  setTopbar("");
  let flipOrder = 0;
  const hero = HERO.map((value, i) =>
    dieHtml({ number: i + 1, value, sides: 6, flip: value !== undefined, delay: value ? 250 + 260 * flipOrder++ : 0 }))
    .join("");

  root.innerHTML = `
    <div class="home">
      <section class="home-intro">
        <ul class="dice hero-dice" aria-hidden="true">${hero}</ul>
        <h1>Trade on what you know.</h1>
        <p>Each player privately sees one hidden die. Buy and sell on a real order book,
          watch the news, and work out what all the dice add up to. No real money.</p>
      </section>

      <div class="home-actions">
        <form class="join-form" id="join-form" novalidate>
          <h2><label for="code">Join a game</label></h2>
          <input class="code-input" id="code" name="code" autocomplete="off" autocapitalize="characters"
            spellcheck="false" maxlength="6" placeholder="ABC123" aria-describedby="join-help join-error" required>
          <p class="empty" id="join-help">Your host shows the room code on their screen.</p>
          <p class="ticket-result rejected" id="join-error" role="alert"></p>
          <button class="button" type="submit">Join game</button>
        </form>

        <section aria-labelledby="host-h">
          <h2 id="host-h">Host a game</h2>
          <div class="mode-list" id="modes"><p class="empty">Loading games…</p></div>
        </section>
      </div>

      <section class="solo" aria-labelledby="solo-h">
        <div>
          <h2 id="solo-h">Practise alone with Chart Replay</h2>
          <p>Real currency charts from 2000 to today, with the dates hidden. Call each week bull or bear,
            say how sure you are, and find out at the end which market it was.</p>
        </div>
        <a class="button" href="#/chart">Play Chart Replay</a>
      </section>

      <p class="home-footnote">No sign-up, no email, no real names. Players get a random name like
        Calm Otter 81, and their seat is a secret key kept in this browser.</p>
    </div>`;

  applyStyles(root);
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

  const modes = await api.getModes();
  const list = root.querySelector("#modes");
  if (!modes.ok) {
    list.innerHTML = `<p class="ticket-result rejected">${esc(modes.error)}</p>`;
    return null;
  }

  list.innerHTML = modes.data
    .sort((a, b) => (a.audience === b.audience ? 0 : a.audience === "Explorer" ? -1 : 1))
    .map((m) => `
      <article class="mode-option">
        <h3>${esc(m.name)} <span class="audience">${m.audience === "Explorer" ? "for younger players" : "for new traders"}</span></h3>
        <p>${esc(m.description)}</p>
        <button class="button quiet" type="button" data-mode="${esc(m.id)}">Host ${esc(m.name)}</button>
      </article>`)
    .join("");

  list.addEventListener("click", async (event) => {
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

  return null;
}
