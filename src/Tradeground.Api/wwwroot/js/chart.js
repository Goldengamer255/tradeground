// Up or Down: one real chart, cut off at "now". The timeframe tabs zoom in and out on that SAME
// chart. Call Up or Down on the timeframe you're viewing, then watch what happened next and find
// out which market and when it was. The server holds the future; this page only gets it after the call.

import * as api from "./api.js";
import { howToButton, showHowToFirstTime } from "./howto.js";
import { esc, notice, setTopbar } from "./ui.js";

const KEY = "tradeground.chart";
const TIMEFRAME = "tradeground.chart.timeframe";
const REPLAY_MS = 70;

const reducedMotion = () => window.matchMedia("(prefers-reduced-motion: reduce)").matches;

function load(name) {
  try {
    return sessionStorage.getItem(name);
  } catch {
    return null;
  }
}

function save(name, value) {
  try {
    if (value) sessionStorage.setItem(name, value);
    else sessionStorage.removeItem(name);
  } catch {
    // Storage blocked: the game still works until the page is refreshed.
  }
}

/** 20 candles of 15 minutes -> "5 hours". */
function duration(timeframe, candles) {
  const minutes = { "1m": 1, "15m": 15, "30m": 30, "1h": 60, "4h": 240 }[timeframe] * candles;
  if (minutes < 60) return plural(minutes, "minute");
  const hours = minutes / 60;
  if (hours < 48) return plural(hours, "hour");
  const days = Math.floor(hours / 24);
  const rest = hours % 24;
  return rest ? `${plural(days, "day")} and ${plural(rest, "hour")}` : plural(days, "day");
}

const plural = (n, word) => `${n} ${n === 1 ? word : `${word}s`}`;

function when(iso) {
  const options = { day: "numeric", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit", timeZone: "UTC", hourCycle: "h23" };
  return `${new Date(iso).toLocaleString("en-GB", options)} UTC`;
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
    this.key = load(KEY);
    this.chart = null;
    this.shown = 0; // How many candles are drawn: grows during the replay.
    this.busy = false;
    this.onKey = (event) => this.handleKey(event);
  }

  async load() {
    const timeframes = await api.getTimeframes();
    if (!timeframes.ok) {
      this.root.innerHTML = notice("Couldn't load Chart Replay", timeframes.error);
      return;
    }
    this.timeframes = timeframes.data;
    this.timeframe = this.timeframes.some((t) => t.id === load(TIMEFRAME)) ? load(TIMEFRAME) : "15m";

    if (this.key) {
      const existing = await api.getChart(this.key, this.timeframe);
      if (existing.ok) {
        this.chart = existing.data;
      } else {
        this.key = null;
      }
    }

    if (!this.chart) {
      const started = await api.startChart(this.timeframe);
      if (!started.ok) {
        this.root.innerHTML = notice("Couldn't start a chart", started.error);
        return;
      }
      this.key = started.data.key;
      this.chart = started.data.chart;
      save(KEY, this.key);
    }

    this.shown = this.chart.candles.length;
    this.build();
    document.addEventListener("keydown", this.onKey);
    showHowToFirstTime("up-or-down");
  }

  close() {
    document.removeEventListener("keydown", this.onKey);
    clearTimeout(this.replayTimer);
    this.resize?.disconnect();
  }

  // ---------- Layout ----------

  build() {
    const tabs = this.timeframes.map((t) => `
      <button type="button" data-timeframe="${esc(t.id)}" title="${esc(`Each candle is ${t.label}`)}">${esc(t.id)}</button>`).join("");

    this.root.innerHTML = `
      <div class="chart-game">
        <div class="chart-head">
          <div>
            <div class="game-title"><h1>Up or down?</h1>${howToButton("up-or-down")}</div>
            <p id="chart-question"></p>
          </div>
          <dl class="stats chart-stats" id="chart-stats"></dl>
        </div>

        <div class="segmented tf-tabs" role="group" aria-label="Candle timeframe" id="tf-tabs">${tabs}</div>

        <figure class="panel chart-panel">
          <svg id="chart" role="img"></svg>
          <figcaption class="empty">The same chart on every timeframe. Prices are shown as an index where "now" is 100.</figcaption>
        </figure>

        <div class="call-row" id="call-row">
          <button type="button" class="call call-up" data-call="Up" aria-keyshortcuts="ArrowUp">
            <span aria-hidden="true">▲</span> Up</button>
          <button type="button" class="call call-down" data-call="Down" aria-keyshortcuts="ArrowDown">
            <span aria-hidden="true">▼</span> Down</button>
        </div>

        <div id="chart-result" role="status" aria-live="polite"></div>
        <p class="empty keys-hint">Keyboard: ↑ or ↓ to call, Enter for the next chart.</p>
      </div>`;

    this.root.querySelector("#tf-tabs").addEventListener("click", (event) => {
      const tab = event.target.closest("[data-timeframe]");
      if (tab && tab.dataset.timeframe !== this.timeframe) this.zoom(tab.dataset.timeframe);
    });
    this.root.querySelector("#call-row").addEventListener("click", (event) => {
      const button = event.target.closest("[data-call]");
      if (button) this.call(button.dataset.call);
    });

    this.resize?.disconnect();
    this.resize = new ResizeObserver(() => this.renderChart());
    this.resize.observe(this.root.querySelector(".chart-panel"));
    this.render();
  }

  render() {
    const c = this.chart;
    const called = c.result !== null;
    const replaying = this.shown < c.candles.length;

    this.root.querySelectorAll("[data-timeframe]").forEach((b) => {
      b.setAttribute("aria-pressed", String(b.dataset.timeframe === this.timeframe));
      b.disabled = this.busy || replaying;
    });
    this.root.querySelector("#chart-question").innerHTML = called
      ? "Switch timeframes to see what happened at every zoom level."
      : `A real chart, stopped at "now". Zoom in and out with the timeframes, then call it:
        will the price be higher or lower <strong>${esc(duration(c.timeframe, c.horizon))}</strong> from now?
        The market and dates stay hidden until you call.`;
    this.root.querySelector("#call-row").hidden = called;
    this.root.querySelectorAll("[data-call]").forEach((b) => {
      b.disabled = this.busy;
    });

    // Show the old tally while the replay plays, or the counters would give the answer away.
    const s = replaying && this.statsBefore ? this.statsBefore : c.stats;
    this.root.querySelector("#chart-stats").innerHTML = `
      <div><dt>Right</dt><dd>${s.right}</dd></div>
      <div><dt>Wrong</dt><dd>${s.wrong}</dd></div>
      <div><dt>Streak</dt><dd>${s.streak}<small> best ${s.bestStreak}</small></dd></div>`;

    this.renderChart();
    this.renderResult(replaying);
  }

  // ---------- The chart ----------

  renderChart() {
    const svg = this.root.querySelector("#chart");
    if (!svg || !this.chart) return;
    const c = this.chart;
    const all = c.candles;
    const candles = all.slice(0, this.shown);
    const slots = c.historyCandles + c.horizon;

    // Draw at the real on-screen size, so text stays readable on a phone instead of scaling down.
    const width = Math.max(280, Math.round(svg.getBoundingClientRect().width) || 1000);
    const height = Math.round(Math.min(420, Math.max(240, width * 0.45)));
    svg.setAttribute("viewBox", `0 0 ${width} ${height}`);
    svg.setAttribute("height", String(height));
    const axis = 52;
    const plotWidth = width - axis;
    const pad = 16;

    // Scale to everything that will be shown, so nothing jumps while the replay plays.
    let min = Math.min(...all.map((k) => k.low));
    let max = Math.max(...all.map((k) => k.high));
    const margin = (max - min) * 0.08 || 50;
    min -= margin;
    max += margin;

    const y = (v) => pad + ((max - v) / (max - min)) * (height - 2 * pad);
    const slot = plotWidth / slots;
    const body = Math.max(2, slot * 0.62);
    const index = (v) => (v / 100).toFixed(1);
    const f = (n) => n.toFixed(1);

    const grid = [0, 1, 2, 3, 4].map((i) => {
      const v = min + ((max - min) * i) / 4;
      return `<line class="grid" x1="0" x2="${plotWidth}" y1="${f(y(v))}" y2="${f(y(v))}"></line>
        <text class="axis" x="${width - 4}" y="${f(y(v))}" text-anchor="end" dominant-baseline="middle">${index(v)}</text>`;
    }).join("");

    const callX = c.historyCandles * slot;
    const callClose = all[c.historyCandles - 1].close;

    const future = `<rect class="future" x="${f(callX)}" y="0" width="${f(plotWidth - callX)}" height="${height}"></rect>
      <line class="now" x1="${f(callX)}" x2="${f(callX)}" y1="0" y2="${height}"></line>`;

    const level = c.result
      ? `<line class="call-level" x1="${f(callX)}" x2="${plotWidth}" y1="${f(y(callClose))}" y2="${f(y(callClose))}"></line>`
      : `<text class="next" x="${f(callX + (plotWidth - callX) / 2)}" y="${f(height / 2)}" text-anchor="middle" dominant-baseline="middle">?</text>`;

    const bars = candles.map((k, i) => {
      const x = i * slot + slot / 2;
      const up = k.close >= k.open;
      const top = y(Math.max(k.open, k.close));
      const h = Math.max(1.5, y(Math.min(k.open, k.close)) - top);
      const fresh = i >= c.historyCandles && i === candles.length - 1 && this.shown < all.length ? " fresh" : "";
      return `<g class="candle ${up ? "up" : "down"}${fresh}">
        <line x1="${f(x)}" x2="${f(x)}" y1="${f(y(k.high))}" y2="${f(y(k.low))}"></line>
        <rect x="${f(x - body / 2)}" y="${f(top)}" width="${f(body)}" height="${f(h)}" rx="1"></rect>
      </g>`;
    }).join("");

    svg.innerHTML = `${grid}${future}${level}${bars}`;

    const last = candles[candles.length - 1];
    svg.setAttribute("aria-label", `Chart of ${candles.length} ${c.timeframe} candles. Latest close ${index(last.close)}.`);
  }

  // ---------- Playing ----------

  /** Shows the current chart at another timeframe. Looking never changes the game. */
  async zoom(timeframe) {
    if (this.busy) return;
    this.busy = true;
    this.timeframe = timeframe;
    save(TIMEFRAME, timeframe);
    this.render();

    const view = await api.getChart(this.key, timeframe);
    this.busy = false;
    if (!view.ok) {
      this.root.querySelector("#chart-result").innerHTML = `<p class="down">${esc(view.error)}</p>`;
      this.render();
      return;
    }

    this.chart = view.data;
    this.shown = this.chart.candles.length;
    this.render();
  }

  /** Deals a brand-new chart, keeping the timeframe the player is on. */
  async deal() {
    if (this.busy) return;
    this.busy = true;
    this.render();

    const next = await api.nextChart(this.key, this.timeframe);
    this.busy = false;
    if (!next.ok) {
      this.root.querySelector("#chart-result").innerHTML = `<p class="down">${esc(next.error)}</p>`;
      this.render();
      return;
    }

    this.chart = next.data;
    this.shown = this.chart.candles.length;
    this.render();
  }

  async call(direction) {
    if (this.busy || this.chart.result) return;
    this.busy = true;
    this.render();

    const response = await api.callChart(this.key, direction, this.timeframe);
    this.busy = false;
    if (!response.ok) {
      this.root.querySelector("#chart-result").innerHTML = `<p class="down">${esc(response.error)}</p>`;
      this.render();
      return;
    }

    this.statsBefore = this.chart.stats;
    this.chart = response.data;
    this.shown = reducedMotion() ? this.chart.candles.length : this.chart.historyCandles;
    this.replay();
  }

  /** Reveals the future one candle at a time: the moment of truth. */
  replay() {
    this.render();
    if (this.shown >= this.chart.candles.length) {
      this.root.querySelector("#next")?.focus();
      return;
    }
    this.replayTimer = setTimeout(() => {
      this.shown += 1;
      this.replay();
    }, REPLAY_MS);
  }

  handleKey(event) {
    if (event.target.closest?.("input, textarea, select") || event.ctrlKey || event.metaKey || event.altKey) return;
    if (document.querySelector("dialog[open]")) return; // Keys belong to the open guide, not the chart behind it.
    if (!this.chart.result && (event.key === "ArrowUp" || event.key === "ArrowDown")) {
      event.preventDefault();
      this.call(event.key === "ArrowUp" ? "Up" : "Down");
    } else if (this.chart.result && event.key === "Enter" && this.shown >= this.chart.candles.length
      && !event.target.closest?.("button, a")) {
      this.deal();
    }
  }

  // ---------- The reveal ----------

  renderResult(replaying) {
    const el = this.root.querySelector("#chart-result");
    const c = this.chart;
    if (!c.result || replaying) {
      el.innerHTML = c.result ? '<p class="chart-wait">Replaying what happened next…</p>' : "";
      return;
    }

    const r = c.result;
    const v = c.reveal;
    // The verdict belongs to the timeframe the call was made on, whichever tab is open now.
    const span = duration(r.timeframe, c.horizon);
    const verdict = r.correct === null ? "Flat" : r.correct ? "Right" : "Wrong";
    const move = r.actual === null
      ? `${v.symbol} ended exactly where it was ${span} later`
      : `${v.symbol} went ${r.actual === "Up" ? "up" : "down"} ${Math.abs(r.changePercent).toFixed(2)}% over the next ${span}`;
    const tone = r.correct === null ? "" : r.correct ? "up" : "down";

    el.innerHTML = `
      <section class="panel chart-verdict ${tone}" aria-labelledby="verdict-h">
        <h2 id="verdict-h">${verdict}. ${esc(move)}.</h2>
        <p>You called ${r.call === "Up" ? "up" : "down"} on the ${esc(r.timeframe)} chart. This was ${esc(v.name)},
          from ${esc(when(v.now))} to ${esc(when(v.decidedAt))}:
          the real price went from ${esc(v.priceNow)} to ${esc(v.endPrice)}.</p>
        <div class="host-controls">
          <button class="button" type="button" id="next">Next chart</button>
          <a class="button quiet" href="#/">Back to start</a>
        </div>
      </section>`;

    el.querySelector("#next").addEventListener("click", () => this.deal());
  }
}
