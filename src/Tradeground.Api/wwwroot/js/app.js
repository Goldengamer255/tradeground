// Hash router: #/ (home), #/play/CODE (player), #/host/CODE (host), #/chart (Chart Replay).
// Hash routes need no server configuration: the server only ever serves index.html.

import { openChart } from "./chart.js";
import { renderHome } from "./home.js";
import { openRoom } from "./room.js";

const root = document.getElementById("app");
let dispose = null;
let generation = 0;

async function route() {
  const mine = ++generation;
  if (dispose) {
    const stop = dispose;
    dispose = null;
    await stop();
  }

  const [view, code] = location.hash.replace(/^#\/?/, "").split("/");
  let next;
  if ((view === "play" || view === "host") && code) next = await openRoom(root, code.toUpperCase(), view);
  else if (view === "chart") next = await openChart(root);
  else next = await renderHome(root);

  // If the player navigated again while this view was loading, close it straight away.
  if (mine !== generation) {
    await next?.();
    return;
  }

  dispose = next;
  root.focus();
}

window.addEventListener("hashchange", route);
route();
