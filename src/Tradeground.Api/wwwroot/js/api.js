// Thin wrapper over the REST API. Every call resolves to { ok, status, data } or
// { ok: false, status, error } with a message a player can act on. Nothing throws.

async function request(method, url, { body, traderKey, hostKey, chartKey } = {}) {
  const headers = {};
  if (body !== undefined) headers["Content-Type"] = "application/json";
  if (traderKey) headers["X-Trader-Key"] = traderKey;
  if (hostKey) headers["X-Host-Key"] = hostKey;
  if (chartKey) headers["X-Chart-Key"] = chartKey;

  let response;
  try {
    response = await fetch(url, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch {
    return { ok: false, status: 0, error: "Can't reach the server. Check your connection and try again." };
  }

  const text = await response.text();
  let data = null;
  try {
    data = text ? JSON.parse(text) : null;
  } catch {
    data = text;
  }

  if (response.ok) return { ok: true, status: response.status, data };
  return { ok: false, status: response.status, data, error: messageFor(response.status, data) };
}

function messageFor(status, data) {
  if (typeof data === "string" && data) return data;
  if (data?.errors) return Object.values(data.errors).flat()[0];
  if (status === 404) return "That room doesn't exist. Check the code with your host.";
  if (status === 401) return "This page doesn't have a seat in that room any more.";
  if (status === 503) return "The room is busy. Try again in a moment.";
  return data?.detail ?? data?.title ?? `The server answered ${status}.`;
}

const room = (code) => `/api/rooms/${encodeURIComponent(code)}`;

export const getModes = () => request("GET", "/api/modes");
export const createRoom = (modeId) => request("POST", "/api/rooms", { body: { modeId } });
export const getRoom = (code) => request("GET", room(code));
export const joinRoom = (code) => request("POST", `${room(code)}/players`);
export const getMe = (code, traderKey) => request("GET", `${room(code)}/me`, { traderKey });
export const placeOrder = (code, traderKey, order) =>
  request("POST", `${room(code)}/orders`, { traderKey, body: order });
export const cancelOrder = (code, traderKey, orderId) =>
  request("DELETE", `${room(code)}/orders/${orderId}`, { traderKey });
export const revealClue = (code, hostKey) => request("POST", `${room(code)}/clues`, { hostKey });
export const settle = (code, hostKey) => request("POST", `${room(code)}/settlement`, { hostKey });

// Chart Replay (solo).
export const getTimeframes = () => request("GET", "/api/charts/timeframes");
export const startChart = (timeframe) => request("POST", "/api/charts", { body: { timeframe } });
export const getChart = (chartKey, timeframe) =>
  request("GET", `/api/charts/mine?timeframe=${encodeURIComponent(timeframe)}`, { chartKey });
export const nextChart = (chartKey, timeframe) => request("POST", "/api/charts/mine/next", { chartKey, body: { timeframe } });
export const callChart = (chartKey, direction, timeframe) =>
  request("POST", "/api/charts/mine/call", { chartKey, body: { direction, timeframe } });
