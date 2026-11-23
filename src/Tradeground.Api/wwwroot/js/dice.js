// The hidden value, drawn as dice, plus the fair-value estimate a player can make from what
// they know. This is the lesson of the game: price = what you know + the average of what you don't.

import { esc, fmt } from "./ui.js";

/** Every die this viewer knows: public news, plus their own private die. Map of die number -> value. */
export function knownDice(publicClues, privateClue) {
  const known = new Map(publicClues.map((c) => [c.die, c.value]));
  if (privateClue) known.set(privateClue.die, privateClue.value);
  return known;
}

/** Expected value and the full possible range, given the known dice. */
export function estimate(mode, known) {
  const sum = [...known.values()].reduce((a, b) => a + b, 0);
  const unknown = mode.diceCount - known.size;
  const perPip = mode.valuePerPip;
  return {
    value: (sum + (unknown * (mode.diceSides + 1)) / 2) * perPip,
    low: (sum + unknown) * perPip,
    high: (sum + unknown * mode.diceSides) * perPip,
    knownCount: known.size,
  };
}

// Pip positions on a 3x3 grid, numbered 1-9 left to right, top to bottom.
const PIPS = { 1: [5], 2: [3, 7], 3: [3, 5, 7], 4: [1, 3, 7, 9], 5: [1, 3, 5, 7, 9], 6: [1, 3, 4, 6, 7, 9] };

function face(value, sides) {
  if (sides !== 6) return esc(value);
  const cells = Array.from({ length: 9 }, (_, i) =>
    PIPS[value].includes(i + 1) ? '<span class="pip"></span>' : "<span></span>");
  return `<span class="pips">${cells.join("")}</span>`;
}

export function dieHtml({ number, value, sides, mine = false, flip = false, word = "die", delay = 0 }) {
  const known = value !== undefined;
  const classes = ["die", known ? "known" : "hidden", mine ? "mine" : "", flip ? "flip" : ""].join(" ");
  const label = known ? `${word} ${number}: ${value}${mine ? " (only you can see this)" : ""}` : `${word} ${number}: hidden`;
  const style = delay ? ` data-delay="${delay}"` : "";
  return `<li class="${classes}" aria-label="${esc(label)}"${style}>
    <span class="die-face">${known ? face(value, sides) : "?"}</span></li>`;
}

/**
 * @param flip dice numbers to animate turning over, in the order they should turn.
 * @param settledAt the final value once the round is over; replaces the estimate.
 */
export function railHtml(mode, words, known, { mineDie, flip = [], settledAt } = {}) {
  const dice = Array.from({ length: mode.diceCount }, (_, i) => {
    const number = i + 1;
    const order = flip.indexOf(number);
    return dieHtml({
      number,
      value: known.get(number),
      sides: mode.diceSides,
      mine: number === mineDie,
      flip: order >= 0,
      delay: order > 0 ? order * 90 : 0,
      word: words.die,
    });
  }).join("");

  if (settledAt !== undefined) {
    const pips = [...known.values()].reduce((a, b) => a + b, 0);
    const detail = mode.valuePerPip === 1
      ? `The ${words.dice} added up to ${fmt(pips)}`
      : `The ${words.dice} added up to ${fmt(pips)}, worth ${fmt(mode.valuePerPip)} each`;
    return `
      <ul class="dice">${dice}</ul>
      <div class="estimate">
        <span class="estimate-label">Final value</span>
        <span class="estimate-value">${fmt(settledAt)}</span>
        <span class="estimate-range">${esc(detail)}</span>
      </div>`;
  }

  const e = estimate(mode, known);
  const range = e.low === e.high
    ? `Every ${words.die} is known`
    : `Anywhere from ${fmt(e.low)} to ${fmt(e.high)}`;

  return `
    <ul class="dice">${dice}</ul>
    <div class="estimate">
      <span class="estimate-label">${esc(words.estimate)}, from ${e.knownCount} of ${mode.diceCount} ${esc(words.dice)}</span>
      <span class="estimate-value">${fmt(e.value)}</span>
      <span class="estimate-range">${range}</span>
    </div>`;
}
