// The trading room, for both players and hosts.
//
// Data flow: REST for actions and catch-up, SignalR for live pushes. On open we SUBSCRIBE first
// and fetch the room state second, so no event can fall into the gap between the two; duplicates
// that arrive twice are dropped by sequence number (trades) or die number (news).
//
// Rendering: the page is built once, then each region (book, news, tape...) re-renders on its
// own. The order ticket is never rebuilt, so a live update can't wipe what a player is typing.

import * as api from "./api.js";
import { estimate, knownDice, railHtml } from "./dice.js";
import { wordsFor } from "./words.js";
import { applyStyles, esc, fmt, forgetSeat, notice, saveSeat, seatFor, setTopbar, signed, toast, trend } from "./ui.js";

const BOOK_DEPTH = 8;
const TAPE_LENGTH = 60;

export async function openRoom(root, code, view) {
  const loaded = await api.getRoom(code);
  if (!loaded.ok) {
    root.innerHTML = loaded.status === 404
      ? notice("Room not found", `There's no room with the code ${code}. Check the code with your host.`)
      : notice("Something went wrong", loaded.error);
    return null;
  }

  const roomView = loaded.data;
  const seat = seatFor(code);

  if (view === "host") {
    if (seat?.role !== "host") {
      root.innerHTML = notice("This page is for the host",
        "Only the browser that created this room can run it.",
        `<a class="button" href="#/play/${esc(code)}">Join as a player</a>`);
      return null;
    }
    return start(root, code, "host", seat.key, roomView);
  }

  if (seat?.role === "player") {
    const me = await api.getMe(code, seat.key);
    if (me.ok) return start(root, code, "player", seat.key, roomView, me.data);
    forgetSeat(code);
  }

  // Arrived by link, or the old seat is gone: offer to join.
  if (roomView.phase !== "Trading") {
    root.innerHTML = notice("This round is over", "Ask your host to start a new game, then join with the new code.");
    return null;
  }

  root.innerHTML = notice(`Join ${roomView.mode.name}`, roomView.mode.description,
    '<button class="button" id="join-now" type="button">Join the game</button> <a class="button quiet" href="#/">Cancel</a>');
  root.querySelector("#join-now").addEventListener("click", async (event) => {
    event.target.disabled = true;
    const joined = await api.joinRoom(code);
    if (!joined.ok) {
      event.target.disabled = false;
      toast(joined.error);
      return;
    }
    saveSeat(code, { role: "player", key: joined.data.traderKey });
    window.dispatchEvent(new HashChangeEvent("hashchange"));
  });
  return null;
}

async function start(root, code, role, key, roomView, me) {
  const room = new Room(root, code, role, key, roomView, me);
  await room.connect();
  return () => room.close();
}

class Room {
  constructor(root, code, role, key, roomView, me) {
    this.root = root;
    this.code = code;
    this.role = role;
    this.key = key;
    this.mode = roomView.mode;
    this.words = wordsFor(this.mode.audience);
    this.me = me ?? null;
    this.book = { lastSequence: -1, bids: [], asks: [] };
    this.clues = new Map();
    this.tape = [];
    this.applyRoomView(roomView);

    const fair = Math.round(this.fairValue().value);
    this.ticket = { side: "Buy", type: "Limit", quantity: 1, price: fair };

    this.build();
  }

  // ---------- Connection ----------

  async connect() {
    const connection = new signalR.HubConnectionBuilder()
      .withUrl("/hubs/market")
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Warning)
      .build();
    this.connection = connection;

    connection.on("bookUpdated", (snapshot) => this.applyBook(snapshot));
    connection.on("tradesExecuted", (trades) => this.applyTrades(trades));
    connection.on("clueRevealed", (clue) => this.applyClue(clue, true));
    connection.on("playerJoined", (count) => {
      this.playerCount = count;
      this.renderPlayerCount();
    });
    connection.on("roundSettled", (settlement) => this.applySettlement(settlement));
    connection.onreconnecting(() => toast("Connection lost. Reconnecting…"));
    connection.onclose(() => {
      if (!this.closing) toast("Lost connection to the game. Refresh the page to rejoin.");
    });
    connection.onreconnected(async () => {
      try {
        await this.catchUp();
        toast("Reconnected.");
      } catch {
        await this.handleLostRoom();
      }
    });

    try {
      await connection.start();
      await this.catchUp();
    } catch {
      await this.handleLostRoom();
    }
  }

  /**
   * Rooms live in server memory for now, so a server restart ends every round. When that
   * happens, say so plainly instead of leaving a page that looks live but isn't.
   */
  async handleLostRoom() {
    const check = await api.getRoom(this.code);
    if (check.status !== 404) {
      toast("Live updates are unavailable. Refresh the page to try again.");
      return;
    }

    this.closing = true;
    await this.connection?.stop();
    if (this.role === "player") forgetSeat(this.code);
    setTopbar("");
    this.root.innerHTML = notice("This room has closed",
      "The game server restarted, which ends every round in progress. Ask your host for a new room code.");
  }

  async catchUp() {
    this.applyBook(await this.connection.invoke("Subscribe", this.code));
    const latest = await api.getRoom(this.code);
    if (latest.ok) {
      this.applyRoomView(latest.data);
      this.renderAll();
    }
    await this.refreshMe();
  }

  async close() {
    this.closing = true;
    await this.connection?.stop();
  }

  // ---------- State updates ----------

  applyRoomView(view) {
    this.phase = view.phase;
    this.playerCount = view.playerCount;
    for (const clue of view.publicClues) this.clues.set(clue.die, clue);
    this.mergeTape(view.recentTrades);
    this.settlement = view.settlement ?? this.settlement ?? null;
  }

  applyBook(snapshot) {
    // Pushes from different requests can arrive out of order; never go backwards.
    if (snapshot.lastSequence < this.book.lastSequence) return;
    this.book = snapshot;
    this.renderBook();
    this.refreshMe();
  }

  applyTrades(trades) {
    this.mergeTape(trades);
    this.renderTape();
  }

  mergeTape(trades) {
    const seen = new Set(this.tape.map((t) => t.sequence));
    const fresh = trades.filter((t) => !seen.has(t.sequence));
    this.tape = [...this.tape, ...fresh].sort((a, b) => b.sequence - a.sequence).slice(0, TAPE_LENGTH);
  }

  applyClue(clue, live) {
    if (this.clues.has(clue.die)) return;
    this.clues.set(clue.die, clue);
    this.renderRail(live ? [clue.die] : []);
    this.renderNews();
    this.renderStats();
    this.renderHostControls();
    if (live) toast(clue.text);
  }

  applySettlement(settlement) {
    if (this.settlement) return; // Both the REST reply and the push deliver it; handle it once.
    const hidden = Array.from({ length: this.mode.diceCount }, (_, i) => i + 1)
      .filter((n) => !this.known().has(n));

    this.phase = "Settled";
    this.settlement = settlement;
    this.renderAll();
    this.renderRail(hidden); // The big reveal: every die nobody had seen turns over, one by one.
    this.root.querySelector("#results")?.scrollIntoView({ behavior: "smooth", block: "start" });
  }

  async refreshMe() {
    if (this.role !== "player") return;
    // Many pushes can arrive close together: keep at most one request in flight plus one queued.
    if (this.meInFlight) {
      this.meQueued = true;
      return;
    }

    this.meInFlight = true;
    const previous = this.me;
    const result = await api.getMe(this.code, this.key);
    this.meInFlight = false;

    if (result.ok) {
      this.me = result.data;
      if (previous && previous.position !== this.me.position && !this.submitting) {
        const change = this.me.position - previous.position;
        toast(`Your order traded: you ${change > 0 ? "bought" : "sold"} ${Math.abs(change)} ${this.mode.unit}.`);
      }
      this.renderStats();
      this.renderOrders();
    }

    if (this.meQueued) {
      this.meQueued = false;
      await this.refreshMe();
    }
  }

  /** Dice this viewer can see: their own and the news, or all of them once the round is settled. */
  known() {
    if (this.settlement) return new Map(this.settlement.dice.map((value, i) => [i + 1, value]));
    return knownDice([...this.clues.values()], this.me?.privateClue);
  }

  fairValue() {
    if (this.settlement) {
      const v = this.settlement.trueValue;
      return { value: v, low: v, high: v, knownCount: this.mode.diceCount };
    }
    return estimate(this.mode, this.known());
  }

  // ---------- Layout ----------

  build() {
    const w = this.words;
    const m = this.mode;
    const isHost = this.role === "host";

    setTopbar(`<span>Room <strong>${esc(this.code)}</strong></span>
      <span class="hide-narrow">${esc(m.name)}</span>
      ${isHost ? "<strong>Host</strong>" : `<strong>${esc(this.me.displayName)}</strong>`}`);

    const hostHead = `
      <section class="host-head" aria-label="Room code">
        <div class="room-code">${esc(this.code)}</div>
        <div>
          <h1>${esc(m.name)}</h1>
          <p>Players open <strong>${esc(location.host)}</strong> and enter this code.</p>
          <p id="player-count" aria-live="polite"></p>
          <div class="host-controls" id="host-controls"></div>
        </div>
      </section>`;

    const playerColumn = `
      <div class="stack">
        <section class="panel panel-you" aria-labelledby="you-h">
          <div class="panel-head"><h2 id="you-h">${esc(w.clueTitle)}</h2></div>
          <p class="clue">${esc(this.me?.privateClue.text)}</p>
          <dl class="stats" id="stats"></dl>
        </section>
        <section class="panel panel-ticket" aria-labelledby="ticket-h">
          <div class="panel-head">
            <h2 id="ticket-h">${esc(w.ticketTitle)}</h2>
            <small>Up to ${m.maxOrderQuantity} at a time, ${m.maxPosition} in total</small>
          </div>
          ${this.ticketHtml()}
        </section>
        <section class="panel panel-orders" aria-labelledby="orders-h">
          <div class="panel-head"><h2 id="orders-h">${esc(w.ordersTitle)}</h2></div>
          <ul class="orders" id="orders"></ul>
        </section>
      </div>`;

    this.root.innerHTML = `
      ${isHost ? hostHead : ""}
      <div id="results"></div>
      <section class="rail" id="rail" aria-label="The hidden ${esc(w.dice)}"></section>
      <div class="floor${isHost ? " host-floor" : ""}">
        ${isHost ? "" : playerColumn}
        <section class="panel panel-book" aria-labelledby="book-h">
          <div class="panel-head">
            <h2 id="book-h">${esc(w.book)}</h2>
            ${isHost ? "" : `<small>${esc(w.bookHint)}</small>`}
          </div>
          <div class="ladder" id="book"></div>
        </section>
        <div class="stack feeds">
          <section class="panel panel-news" aria-labelledby="news-h">
            <h2 id="news-h">${esc(w.news)}</h2>
            <ul class="feed news" id="news" aria-live="polite"></ul>
          </section>
          <section class="panel panel-tape" aria-labelledby="tape-h">
            <h2 id="tape-h">${esc(w.trades)}</h2>
            <ul class="feed tape" id="tape"></ul>
          </section>
        </div>
      </div>`;

    this.wireEvents();
    this.renderAll();
  }

  renderAll() {
    this.renderRail();
    this.renderBook();
    this.renderNews();
    this.renderTape();
    this.renderStats();
    this.renderOrders();
    this.renderTicket();
    this.renderPlayerCount();
    this.renderHostControls();
    this.renderResults();
  }

  region(id) {
    return this.root.querySelector(`#${id}`);
  }

  // ---------- Regions ----------

  renderRail(flip = []) {
    const rail = this.region("rail");
    rail.innerHTML = railHtml(this.mode, this.words, this.known(), {
      mineDie: this.me?.privateClue.die,
      flip,
      settledAt: this.settlement?.trueValue,
    });
    applyStyles(rail);
  }

  renderBook() {
    const el = this.region("book");
    if (!el) return;
    const w = this.words;
    const asks = this.book.asks.slice(0, BOOK_DEPTH);
    const bids = this.book.bids.slice(0, BOOK_DEPTH);
    const biggest = Math.max(1, ...asks.map((l) => l.quantity), ...bids.map((l) => l.quantity));
    const clickable = this.role === "player" && this.phase === "Trading";

    const level = (l, side) => {
      const width = Math.round((l.quantity / biggest) * 100);
      const qty = `<span class="qty-${side}">${fmt(l.quantity)}</span>`;
      const action = side === "ask" ? "buy" : "sell";
      const label = `${side === "ask" ? w.asks : w.bids}: ${l.quantity} at ${l.price}. ${clickable ? `Select to ${action} at ${l.price}.` : ""}`;
      const tag = clickable ? "button" : "div";
      const attrs = clickable ? ` type="button" data-price="${l.price}" data-side="${side === "ask" ? "Buy" : "Sell"}"` : "";
      return `<${tag} class="level ${side}"${attrs} aria-label="${esc(label)}">
        <span class="bar" data-width="${width}"></span>
        ${side === "bid" ? qty : "<span></span>"}
        <span class="price">${fmt(l.price)}</span>
        ${side === "ask" ? qty : ""}
      </${tag}>`;
    };

    const bestAsk = asks[0]?.price;
    const bestBid = bids[0]?.price;
    let middle;
    if (bestAsk === undefined && bestBid === undefined) middle = "Nothing on the book yet. Place the first order.";
    else if (bestAsk === undefined) middle = `No ${w.asks.toLowerCase()} yet`;
    else if (bestBid === undefined) middle = `No ${w.bids.toLowerCase()} yet`;
    else middle = `Spread ${fmt(bestAsk - bestBid)}, mid ${fmt((bestAsk + bestBid) / 2)}`;

    el.innerHTML = `
      <div class="ladder-head" aria-hidden="true"><span>${esc(w.bids)}</span><span>Price</span><span>${esc(w.asks)}</span></div>
      ${[...asks].reverse().map((l) => level(l, "ask")).join("")}
      <div class="spread">${middle}</div>
      ${bids.map((l) => level(l, "bid")).join("")}`;
    applyStyles(el);
  }

  renderNews() {
    const clues = [...this.clues.values()].reverse();
    this.region("news").innerHTML = clues.length
      ? clues.map((c) => `<li><span class="tag" aria-hidden="true">${c.die}</span>${esc(c.text)}</li>`).join("")
      : `<li class="empty">${esc(this.words.noNews)}</li>`;
  }

  renderTape() {
    const unit = this.mode.unit;
    this.region("tape").innerHTML = this.tape.length
      ? this.tape.map((t) => {
        const bought = t.aggressorSide === "Buy";
        const verb = bought ? '<span class="side-buy">Bought</span>' : '<span class="side-sell">Sold</span>';
        return this.mode.audience === "Explorer"
          ? `<li><span>${verb}</span><span>${fmt(t.quantity)} ${esc(unit)}</span><span>${fmt(t.price)} coins each</span></li>`
          : `<li><span>${verb}</span><span>${fmt(t.quantity)} @ ${fmt(t.price)}</span><span class="when">#${t.sequence}</span></li>`;
      }).join("")
      : `<li class="empty">${esc(this.words.noTrades)}</li>`;
  }

  renderStats() {
    const el = this.region("stats");
    if (!el || !this.me) return;
    const w = this.words;
    const fair = this.fairValue().value;
    const pnl = this.me.cash + this.me.position * fair - this.mode.startingCash;
    el.innerHTML = `
      <div><dt>${esc(w.cash)}</dt><dd>${fmt(this.me.cash)}</dd></div>
      <div><dt>${esc(w.position)}</dt><dd>${signed(this.me.position)} <span class="empty">${esc(this.mode.unit)}</span></dd></div>
      <div><dt>${this.settlement ? "Final profit" : esc(w.atFair)}</dt><dd class="${trend(pnl)}">${signed(Math.round(pnl))}</dd></div>`;
  }

  renderOrders() {
    const el = this.region("orders");
    if (!el || !this.me) return;
    const orders = this.me.openOrders;
    if (this.phase !== "Trading") {
      el.innerHTML = '<li class="empty">The round is over. Any orders still waiting were cancelled.</li>';
      return;
    }
    el.innerHTML = orders.length
      ? orders.map((o) => `
        <li>
          <span><span class="side-${o.side.toLowerCase()}">${o.side}</span> ${fmt(o.remaining)} at ${fmt(o.price)}</span>
          <button class="button quiet" type="button" data-cancel="${o.orderId}"
            aria-label="Cancel ${o.side.toLowerCase()} ${o.remaining} at ${o.price}">Cancel</button>
        </li>`).join("")
      : `<li class="empty">${esc(this.words.noOrders)}</li>`;
  }

  renderPlayerCount() {
    const el = this.region("player-count");
    if (!el) return;
    const n = this.playerCount;
    el.textContent = n === 0 ? "Waiting for players to join." : `${n} ${n === 1 ? "player has" : "players have"} joined.`;
  }

  renderHostControls() {
    const el = this.region("host-controls");
    if (!el) return;
    if (this.phase !== "Trading") {
      el.innerHTML = '<a class="button" href="#/">Host another game</a>';
      return;
    }
    const left = this.mode.diceCount - this.clues.size;
    const w = this.words;
    el.innerHTML = `
      <button class="button" type="button" id="reveal" ${left === 0 ? "disabled" : ""}>
        ${left === 0 ? `Every ${esc(w.die)} revealed` : `Reveal a ${esc(w.die)} (${left} left)`}
      </button>
      <button class="button quiet" type="button" id="settle">End the round</button>`;
  }

  renderResults() {
    const el = this.region("results");
    if (!el) return;
    const s = this.settlement;
    if (!s) {
      el.innerHTML = "";
      return;
    }

    const mine = this.me?.displayName;
    const rows = s.results.map((r) => `
      <tr class="${r.displayName === mine ? "me" : ""}">
        <td>${r.rank}</td>
        <td>${esc(r.displayName)}${r.displayName === mine ? " (you)" : ""}</td>
        <td>${signed(r.position)}</td>
        <td>${fmt(r.cash)}</td>
        <td class="${trend(r.profitAndLoss)}">${signed(r.profitAndLoss)}</td>
      </tr>`).join("");

    const me = s.results.find((r) => r.displayName === mine);
    const explain = me
      ? `You finished holding ${signed(me.position)} ${esc(this.mode.unit)} and ${fmt(me.cash)} ${esc(this.words.cash.toLowerCase())}.
        ${fmt(me.cash)} ${me.position < 0 ? "−" : "+"} ${fmt(Math.abs(me.position))} × ${fmt(s.trueValue)} − ${fmt(this.mode.startingCash)} to start = ${signed(me.profitAndLoss)}.
        Every win is someone else's loss: the profits add up to zero.`
      : "Every win is someone else's loss: the profits add up to zero.";

    el.innerHTML = `
      <section class="panel results" aria-labelledby="results-h">
        <div class="panel-head"><h2 id="results-h">Round over</h2></div>
        <p class="results-value">Settled at ${fmt(s.trueValue)}</p>
        <p class="results-explain">${explain}</p>
        <table>
          <thead><tr><th>Rank</th><th>Player</th><th>${esc(this.words.position)}</th><th>${esc(this.words.cash)}</th><th>Profit</th></tr></thead>
          <tbody>${rows || '<tr><td colspan="5">Nobody joined this round.</td></tr>'}</tbody>
        </table>
      </section>`;
  }

  // ---------- Order ticket ----------

  ticketHtml() {
    const w = this.words;
    const m = this.mode;
    const typeToggle = w.marketOrders
      ? `<div class="segmented" role="group" aria-label="Order type">
          <button type="button" data-type="Limit">Limit</button>
          <button type="button" data-type="Market">Market</button>
        </div>`
      : "";

    return `
      <form class="ticket" id="ticket" novalidate>
        <div class="segmented" role="group" aria-label="Buy or sell">
          <button type="button" class="buy" data-side="Buy">Buy</button>
          <button type="button" class="sell" data-side="Sell">Sell</button>
        </div>
        ${typeToggle}
        <div class="ticket-row">
          <div class="field">
            <label for="quantity">${esc(w.quantity)}</label>
            <input id="quantity" type="number" inputmode="numeric" min="1" max="${m.maxOrderQuantity}" step="1" value="${this.ticket.quantity}">
          </div>
          <div class="field" id="price-field">
            <label for="price">${esc(w.price)}</label>
            <input id="price" type="number" inputmode="numeric" min="1" step="1" value="${this.ticket.price}">
          </div>
        </div>
        <button class="button" type="submit" id="submit"></button>
        <p class="ticket-result" id="ticket-result" role="status"></p>
      </form>`;
  }

  renderTicket() {
    const form = this.region("ticket");
    if (!form) return;
    const t = this.ticket;
    const isMarket = t.type === "Market";

    form.querySelectorAll("[data-side]").forEach((b) => b.setAttribute("aria-pressed", String(b.dataset.side === t.side)));
    form.querySelectorAll("[data-type]").forEach((b) => b.setAttribute("aria-pressed", String(b.dataset.type === t.type)));
    form.querySelector("#price-field").hidden = isMarket;

    const submit = form.querySelector("#submit");
    const verb = t.side === "Buy" ? "Buy" : "Sell";
    submit.className = `button ${t.side === "Buy" ? "buy" : "sell"}`;
    submit.textContent = isMarket ? `${verb} ${t.quantity} at the best price` : `${verb} ${t.quantity} at ${t.price}`;
    submit.disabled = this.phase !== "Trading" || this.submitting;
  }

  setTicket(changes) {
    Object.assign(this.ticket, changes);
    const form = this.region("ticket");
    if (changes.price !== undefined) form.querySelector("#price").value = this.ticket.price;
    if (changes.quantity !== undefined) form.querySelector("#quantity").value = this.ticket.quantity;
    this.renderTicket();
  }

  async submitOrder() {
    const t = this.ticket;
    const quantity = Number(t.quantity);
    const price = Number(t.price);

    if (!Number.isInteger(quantity) || quantity < 1 || quantity > this.mode.maxOrderQuantity) {
      this.showTicketResult(`Choose a whole number from 1 to ${this.mode.maxOrderQuantity}.`, true);
      return;
    }
    if (t.type === "Limit" && (!Number.isInteger(price) || price < 1)) {
      this.showTicketResult("Choose a whole-number price of 1 or more.", true);
      return;
    }

    this.submitting = true;
    this.renderTicket();
    const response = await api.placeOrder(this.code, this.key, {
      side: t.side,
      type: t.type,
      quantity,
      limitPrice: t.type === "Limit" ? price : null,
    });
    await this.refreshMe();
    this.submitting = false;
    this.renderTicket();

    if (!response.ok) {
      this.showTicketResult(response.error, true);
      return;
    }

    const r = response.data;
    if (r.status === "Rejected") {
      this.showTicketResult(r.rejectReason, true);
      return;
    }

    const did = t.side === "Buy" ? "Bought" : "Sold";
    const filled = r.fills.reduce((sum, f) => sum + f.quantity, 0);
    const avg = filled ? r.fills.reduce((sum, f) => sum + f.price * f.quantity, 0) / filled : 0;
    const atPrice = r.fills.length === 1 ? `at ${fmt(r.fills[0].price)}` : `at an average of ${fmt(Math.round(avg * 10) / 10)}`;
    const messages = {
      Filled: `${did} ${filled} ${atPrice}.`,
      PartiallyFilled: `${did} ${filled} ${atPrice}. The other ${r.restingQuantity} wait on the book at ${fmt(price)}.`,
      Resting: `Nobody matched yet. Your order for ${r.restingQuantity} at ${fmt(price)} waits on the book.`,
      RemainderCancelled: filled
        ? `${did} ${filled} ${atPrice}. There was nothing left to trade with, so the other ${quantity - filled} were cancelled.`
        : "There was nothing to trade with, so the order was cancelled.",
    };
    this.showTicketResult(messages[r.status] ?? r.status, false);
  }

  showTicketResult(message, rejected) {
    const el = this.region("ticket-result");
    el.textContent = message;
    el.classList.toggle("rejected", rejected);
  }

  // ---------- Events ----------

  wireEvents() {
    const root = this.root;

    root.querySelector("#ticket")?.addEventListener("submit", (event) => {
      event.preventDefault();
      this.submitOrder();
    });

    root.querySelector("#ticket")?.addEventListener("click", (event) => {
      const side = event.target.closest("[data-side]");
      if (side) this.setTicket({ side: side.dataset.side });
      const type = event.target.closest("[data-type]");
      if (type) this.setTicket({ type: type.dataset.type });
    });

    root.querySelector("#quantity")?.addEventListener("input", (event) => {
      this.ticket.quantity = event.target.value === "" ? "" : Number(event.target.value);
      this.renderTicket();
    });

    root.querySelector("#price")?.addEventListener("input", (event) => {
      this.ticket.price = event.target.value === "" ? "" : Number(event.target.value);
      this.renderTicket();
    });

    // Click a price level: trade against it. An ask becomes a buy at that price, and vice versa.
    root.querySelector("#book").addEventListener("click", (event) => {
      const level = event.target.closest("[data-price]");
      if (!level) return;
      this.setTicket({ side: level.dataset.side, type: "Limit", price: Number(level.dataset.price) });
      root.querySelector("#quantity").focus();
    });

    root.querySelector("#orders")?.addEventListener("click", async (event) => {
      const button = event.target.closest("[data-cancel]");
      if (!button) return;
      button.disabled = true;
      const result = await api.cancelOrder(this.code, this.key, button.dataset.cancel);
      if (!result.ok) {
        button.disabled = false;
        toast(result.status === 404 ? "That order already traded or was cancelled." : result.error);
      }
      await this.refreshMe();
    });

    root.querySelector("#host-controls")?.addEventListener("click", (event) => {
      if (event.target.closest("#reveal")) this.reveal(event.target.closest("#reveal"));
      if (event.target.closest("#settle")) this.confirmSettle(event.target.closest("#settle"));
    });
  }

  async reveal(button) {
    button.disabled = true;
    const result = await api.revealClue(this.code, this.key);
    if (result.ok) this.applyClue(result.data, true);
    else toast(result.error);
    this.renderHostControls();
  }

  // Ending a round can't be undone, so the button asks twice instead of opening a dialog.
  async confirmSettle(button) {
    if (button.dataset.armed !== "true") {
      button.dataset.armed = "true";
      button.textContent = "Click again to end the round";
      setTimeout(() => {
        if (button.isConnected) {
          button.dataset.armed = "false";
          button.textContent = "End the round";
        }
      }, 4000);
      return;
    }

    button.disabled = true;
    const result = await api.settle(this.code, this.key);
    if (result.ok) this.applySettlement(result.data);
    else {
      button.disabled = false;
      toast(result.error);
    }
  }
}
