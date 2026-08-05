// The page's behaviour: ask the host for state, render whole states when they arrive.
// Rendering is replace-not-patch on purpose - the host sends complete states, so the UI
// can never drift out of sync with a missed delta.

import { bridge } from "./bridge.js";

const $ = (id) => document.getElementById(id);

function renderState(s) {
  $("tool-version").textContent = s.toolVersion;
  $("s-attached").innerHTML = s.attached
    ? `<span class="ok">attached</span> (pid ${s.processId})`
    : `<span class="bad">not attached</span>`;
  $("s-ingame").textContent = s.inGame ? "yes" : "no";
  $("s-entities").textContent = s.inGame ? String(s.entityCount) : "–";
  $("s-statics").textContent = `${s.staticsFound} / ${s.staticsTotal}`;
  $("s-schema").textContent = s.gameVersion;
}

bridge.on("state", renderState);

// The raw traffic view, newest first, capped so a long session cannot grow it unbounded.
const log = $("log");
bridge.onAny((message) => {
  const line = `${new Date().toLocaleTimeString()}  ${JSON.stringify(message)}`;
  log.textContent = `${line}\n${log.textContent}`.split("\n").slice(0, 200).join("\n");
});

$("refresh").addEventListener("click", () => bridge.send({ type: "getState" }));

if (bridge.connected) {
  bridge.send({ type: "hello" });
} else {
  // Opened in a plain browser for UI work: render a fake state so layout is editable
  // without the host running.
  renderState({
    toolVersion: "browser preview",
    gameVersion: "(no host - open via PoEformance.App --config)",
    attached: false,
    processId: 0,
    inGame: false,
    entityCount: 0,
    staticsFound: 0,
    staticsTotal: 6,
  });
}
