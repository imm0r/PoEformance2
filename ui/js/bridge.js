// The one place that touches window.chrome.webview. Everything else imports { bridge }
// and speaks typed messages, so the transport can be faked in a plain browser tab.
//
// Protocol: objects with a `type` field, both directions. The host answers a request with
// at most one message; unsolicited pushes are allowed and route by type the same way.

const listeners = new Map(); // type -> Set<fn>
const rawListeners = new Set(); // every message, for the debug log

const webview = window.chrome?.webview ?? null;

if (webview) {
  webview.addEventListener("message", (event) => {
    const message = event.data;
    for (const fn of rawListeners) fn(message);
    const set = listeners.get(message?.type);
    if (set) for (const fn of set) fn(message);
  });
}

export const bridge = {
  /** True when hosted in the real config window (vs. opened in a browser for UI work). */
  get connected() {
    return webview !== null;
  },

  /** Sends one message object to the host. A no-op in a plain browser. */
  send(message) {
    webview?.postMessage(message);
  },

  /** Subscribes to messages of one type. Returns an unsubscribe function. */
  on(type, fn) {
    if (!listeners.has(type)) listeners.set(type, new Set());
    listeners.get(type).add(fn);
    return () => listeners.get(type).delete(fn);
  },

  /** Subscribes to every message - the debug log's hook. */
  onAny(fn) {
    rawListeners.add(fn);
    return () => rawListeners.delete(fn);
  },
};
