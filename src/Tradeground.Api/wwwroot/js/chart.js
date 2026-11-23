// Chart Replay: a solo game on a real currency chart. Guess whether next week closes higher,
// and how sure you are. The server holds the future; this page only ever sees revealed weeks.

import * as api from "./api.js";
import { esc, fmt, notice, setTopbar, signed, trend } from "./ui.js";

const KEY = "tradeground.chart";

// Each choice is a stated chance that the price goes UP. Points mirror the server's scoring rule.
const CHOICES = [
  { id: "StrongBear", label: "Strong bear", chance: 10, key: "1" },
  { id: "Bear", label: "Bear", chance: 30, key: "2" },
  { id: "Unsure", label: "Not sure", chance: 50, key: "3" },
  { id: "Bull", label: "Bull", chance: 70, key: "4" },
  { id: "StrongBull", label: "Strong bull", chance: 90, key: "5" },
];

const pointsIf = (chance, up) => 100 - ((chance - (up ? 100 : 0)) ** 2) / 25;
const labelOf = (id) => CHOICES.find((c) => c.id === id)?.label ?? id;

function loadKey() {
  try {
    return sessionStorage.getItem(KEY);
  } catch {
    return null;
  }
}

function saveKey(key) {
  try {
    if (key) sessionStorage.setItem(KEY, key);
    else sessionStorage.removeItem(KEY);
  } catch {
    // Storage blocked: the game still works until the page is refreshed.
  }
}

export async function openChart(root) {
  setTopbar("<span>Chart Replay</span>");
  const game = new ChartGame(root);
  await game.load();
  return () => game.close();
}

class ChartGame {
  constructor(root) {
    this.root = root;
    this.key = loadKey();
    this.chart = null;
    this.busy = false;
    this.onKey = (event) => this.handleKey(event);
  }

  async load() {
    if (this.key) {
      const existing = await api.getChart(this.key);
      if (existing.ok) {
        this.chart = existing.data;
      } else {
        this.key = null;
      }
    }

    if (!this.chart && !(await this.startNew())) return;
    this.build();
    document.addEventListener("keydown", this.onKey);
  }

  async startNew() {
    const started = await api.startChart();
    if (!started.ok) {
      this.root.innerHTML = notice("Couldn't start a chart", started.error);
      return false;
    }
    this.key = started.data.key;
    this.chart = started.data.chart;
    saveKey(this.key);
    return true;
  }

  close() {
    document.removeEventListener("keydown", this.onKey);
    this.resize?.disconnect();
  }

  // ---------- Layout ----------

  build() {
    const buttons = CHOICES.map((c) => `
      <button type="button" class="predict-${c.id.toLowerCase()}" data-prediction="${c.id}"
        aria-keyshortcuts="${c.key}">
        <span class="predict-label">${esc(c.label)}</span>
        <span class="predict-odds">Up ${signed(pointsIf(c.chance, true))}<br>Down ${signed(pointsIf(c.chance, false))}</span>
      </button>`).join("");

    this.root.innerHTML = `
      <div class="chart-game">
        <div class="chart-head">
          <div>
            <h1>Where does it go next week?</h1>
            <p>A real currency chart, one candle per week. Which currencies and which years stay
              hidden until the end, so all you have is the chart.</p>
          </div>
          <dl class="stats chart-stats" id="chart-stats"></dl>
        </div>
        <figure class="panel chart-panel">
          <svg id="chart" role="img"></svg>
          <figcaption class="empty">Prices shown as an index: the first week closes at 100.</figcaption>
        </figure>
        <div class="predict" id="predict" role="group" aria-label="Your call for next week (keys 1 to 5)">${buttons}</div>
        <p class="chart-feedback" id="chart-feedback" role="status"></p>
        <div id="chart-end"></div>
        <details class="how">
          <summary>How the points work</summary>
          <p>Each button says how likely you think an up-week is: Strong bear is 10%, Bear 30%,
            Not sure 50%, Bull 70%, Strong bull 90%. Being confident and right earns the most, but being
            confident and wrong costs more than twice as much. "Not sure" always scores zero.</p>
          <p>That makes honesty the best strategy: over many weeks, the choice that matches what you really
            believe scores highest. Forecasters call this being <em>calibrated</em>.</p>
        </details>
      </div>`;

    this.root.querySelector("#predict").addEventListener("click", (event) => {
      const button = event.target.closest("[data-prediction]");
      if (button) this.guess(button.dataset.prediction);
    });

    this.resize?.disconnect();
    this.resize = new ResizeObserver(() => this.renderChart(false));
    this.resize.observe(this.root.querySelector(".chart-panel"));
    this.render();
  }

  render(fresh = false) {
    this.renderStats();
    this.renderChart(fresh);
    this.renderEnd();
    const finished = this.chart.reveal !== null;
    this.root.querySelector("#predict").hidden = finished;
    this.root.querySelectorAll("[data-prediction]").forEach((b) => {
      b.disabled = this.busy || finished;
    });
  }

  renderStats() {
    const c = this.chart;
    const round = Math.min(c.roundsPlayed + 1, c.rounds);
    this.root.querySelector("#chart-stats").innerHTML = `
      <div><dt>Week</dt><dd>${c.reveal ? c.rounds : round} of ${c.rounds}</dd></div>
      <div><dt>Score</dt><dd class="${trend(c.score)}">${signed(c.score)}</dd></div>`;
  }

  // ---------- The chart ----------

  renderChart(fresh) {
    const svg = this.root.querySelector("#chart");
    const c = this.chart;
    const candles = c.candles;
    const slots = c.historyCandles + c.rounds;
    // Draw at the real on-screen size, so text stays readable on a phone instead of scaling down.
    const width = Math.max(280, Math.round(svg.getBoundingClientRect().width) || 1000);
    const height = Math.round(Math.min(420, Math.max(240, width * 0.45)));
    svg.setAttribute("viewBox", `0 0 ${width} ${height}`);
    svg.setAttribute("height", String(height));
    const axis = 52;
    const plotWidth = width - axis;
    const pad = 16;

    const lows = candles.map((k) => k.low);
    const highs = candles.map((k) => k.high);
    let min = Math.min(...lows);
    let max = Math.max(...highs);
    const margin = (max - min) * 0.08 || 50;
    min -= margin;
    max += margin;

    const y = (v) => pad + ((max - v) / (max - min)) * (height - 2 * pad);
    const slot = plotWidth / slots;
    const body = Math.max(2, slot * 0.62);
    const index = (v) => (v / 100).toFixed(1);

    const grid = [0, 1, 2, 3, 4].map((i) => {
      const v = min + ((max - min) * i) / 4;
      const gy = y(v).toFixed(1);
      return `<line class="grid" x1="0" x2="${plotWidth}" y1="${gy}" y2="${gy}"></line>
        <text class="axis" x="${width - 4}" y="${gy}" text-anchor="end" dominant-baseline="middle">${index(v)}</text>`;
    }).join("");

    const bars = candles.map((k, i) => {
      const x = i * slot + slot / 2;
      const up = k.close >= k.open;
      const top = y(Math.max(k.open, k.close));
      const h = Math.max(1.5, y(Math.min(k.open, k.close)) - top);
      const revealedWeek = i >= c.historyCandles;
      const classes = [up ? "up" : "down", revealedWeek ? "revealed" : "", fresh && i === candles.length - 1 ? "fresh" : ""].join(" ");
      return `<g class="candle ${classes}">
        <line x1="${x.toFixed(1)}" x2="${x.toFixed(1)}" y1="${y(k.high).toFixed(1)}" y2="${y(k.low).toFixed(1)}"></line>
        <rect x="${(x - body / 2).toFixed(1)}" y="${top.toFixed(1)}" width="${body.toFixed(1)}" height="${h.toFixed(1)}" rx="1"></rect>
      </g>`;
    }).join("");

    // The line between what's known and what isn't, and the week being guessed.
    const nowX = candles.length * slot;
    const future = c.reveal
      ? ""
      : `<rect class="future" x="${nowX.toFixed(1)}" y="0" width="${(plotWidth - nowX).toFixed(1)}" height="${height}"></rect>
         <line class="now" x1="${nowX.toFixed(1)}" x2="${nowX.toFixed(1)}" y1="0" y2="${height}"></line>
         <text class="next" x="${(nowX + slot / 2).toFixed(1)}" y="${(height / 2).toFixed(1)}" text-anchor="middle" dominant-baseline="middle">?</text>`;

    const startGuessing = c.historyCandles * slot;
    const marker = `<line class="start-line" x1="${startGuessing.toFixed(1)}" x2="${startGuessing.toFixed(1)}" y1="0" y2="${height}"></line>`;

    svg.innerHTML = `${grid}${future}${marker}${bars}`;

    const last = candles[candles.length - 1];
    const previous = candles[candles.length - 2];
    const change = ((last.close - previous.close) / previous.close) * 100;
    svg.setAttribute("aria-label",
      `Chart of ${candles.length} weekly candles. Latest close ${index(last.close)}, ${change >= 0 ? "up" : "down"} ${Math.abs(change).toFixed(2)}% on the week.`);
  }

  // ---------- Guessing ----------

  async guess(prediction) {
    if (this.busy || this.chart.reveal) return;
    this.busy = true;
    this.render();

    const result = await api.guessChart(this.key, prediction);
    this.busy = false;

    if (!result.ok) {
      this.feedback(result.error, "down");
      this.render();
      return;
    }

    this.chart = result.data.chart;
    const g = result.data.guess;
    const moved = g.move === "Flat"
      ? "Next week closed exactly where it was."
      : `Next week went ${g.move === "Up" ? "up" : "down"} ${Math.abs(g.changePercent).toFixed(2)}%.`;
    this.feedback(`${moved} You said ${labelOf(g.prediction)}: ${signed(g.points)} points.`, trend(g.points));
    this.render(true);
  }

  feedback(message, tone) {
    const el = this.root.querySelector("#chart-feedback");
    el.textContent = message;
    el.className = `chart-feedback ${tone ?? ""}`;
  }

  handleKey(event) {
    if (event.target.closest?.("input, textarea, select") || event.ctrlKey || event.metaKey || event.altKey) return;
    const choice = CHOICES.find((c) => c.key === event.key);
    if (choice) this.guess(choice.id);
  }

  // ---------- The reveal ----------

  renderEnd() {
    const el = this.root.querySelector("#chart-end");
    const r = this.chart.reveal;
    if (!r) {
      el.innerHTML = "";
      return;
    }

    const date = (iso) => new Date(`${iso}T00:00:00Z`)
      .toLocaleDateString("en-GB", { day: "numeric", month: "short", year: "numeric", timeZone: "UTC" });
    const pair = `${r.symbol.slice(0, 3)}/${r.symbol.slice(3)}`;
    const plural = (n, word) => `${n} ${n === 1 ? word : `${word}s`}`;
    const extras = [
      r.notSure ? `said Not sure ${r.notSure === 1 ? "once" : `${r.notSure} times`}` : "",
      r.flat ? `${plural(r.flat, "week")} didn't move` : "",
    ].filter(Boolean);
    const tally = `You called ${r.right} right and ${r.wrong} wrong${extras.length ? `, and ${extras.join(", and ")}` : ""}.`;

    el.innerHTML = `
      <section class="panel results chart-reveal" aria-labelledby="reveal-h">
        <h2 id="reveal-h">It was ${esc(pair)}, ${esc(date(r.firstWeek))} to ${esc(date(r.lastWeek))}</h2>
        <p>${esc(r.name)}: the real rate went from ${esc(r.firstClose)} to ${esc(r.lastClose)}.</p>
        <p class="results-value ${trend(this.chart.score)}">${signed(this.chart.score)} points</p>
        <p>${tally} The best possible score was ${fmt(r.bestPossible)}, by being certain and right every single week.</p>
        <p class="results-explain">Weekly currency moves are close to a coin flip. Saying "Not sure" when
          you are is a skill: the players who score well over many charts are the ones who are only
          confident when they have a reason to be.</p>
        <div class="host-controls">
          <button class="button" type="button" id="again">Play another chart</button>
          <a class="button quiet" href="#/">Back to start</a>
        </div>
      </section>`;

    el.querySelector("#again").addEventListener("click", async () => {
      saveKey(null);
      this.chart = null;
      if (await this.startNew()) {
        this.build();
        this.feedback("");
      }
    });
  }
}
