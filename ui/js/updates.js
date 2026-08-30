// The update tab: what is running, what is published, what changed, and the two buttons.
//
// It holds NO state of its own beyond "is a control being used right now". Everything drawn
// here comes off the host's once-a-second state push, so the page cannot claim a download is
// running that the host has already finished or failed - which is the failure mode a
// page-side copy of the progress would have, and the one that costs the most trust in a
// feature that replaces the tool's own files.

import { bridge } from "./bridge.js";

const $ = (id) => document.getElementById(id);

/** Bytes as the download shows them. Megabytes throughout: the asset is never smaller. */
const mb = (bytes) => `${(bytes / (1024 * 1024)).toFixed(0)} MB`;

export class UpdatePanel {
  constructor() {
    // Set while a control here is in use, so an incoming state does not fight the checkbox.
    // The same guard the overlay card uses, and for the same reason.
    this.editingUntil = 0;
    this.enabled = true;

    $("up-check").addEventListener("click", () => bridge.send({ type: "checkUpdate" }));
    $("up-download").addEventListener("click", () => bridge.send({ type: "downloadUpdate" }));
    $("up-skip").addEventListener("click", () => bridge.send({ type: "skipUpdate" }));

    // The one button that ends the process. Confirmed, because it does: the tool closes,
    // a script replaces its files, and it starts again - which is not what somebody who
    // meant to press "Download" is expecting to happen mid-map.
    $("up-install").addEventListener("click", () => {
      if (window.confirm(
        "PoEformance will close, replace its own files, and start again.\n\n"
        + "Anything it is doing right now stops. Continue?")) {
        bridge.send({ type: "installUpdate" });
      }
    });

    $("up-enabled").addEventListener("change", () => {
      this.editingUntil = Date.now() + 1500;
      bridge.send({ type: "setUpdateSettings", payload: { enabled: $("up-enabled").checked } });
    });
  }

  /** Renders the whole panel from one state block. */
  set(u) {
    if (!u) return;

    $("up-current").textContent = u.current;
    $("up-available").textContent = u.available || "–";
    $("up-checked").textContent = u.checked;
    $("up-status").textContent = u.busy ? "asking GitHub…" : u.status;
    $("up-status").className = {
      Available: "ok",
      UpToDate: "dim",
      Failed: "bad",
    }[u.verdict] ?? "dim";

    if (Date.now() >= this.editingUntil) $("up-enabled").checked = u.enabled;

    // The dot on the tab, which is the whole notification when this tab is not the one open.
    $("up-dot").hidden = !u.offering;

    this.outcome(u);
    this.buttons(u);
    this.progress(u);
    this.notes(u);
  }

  /** The notice about the update that has already happened. */
  outcome(u) {
    const box = $("up-outcome");
    if (!u.outcome) {
      box.hidden = true;
      return;
    }

    box.hidden = false;
    if (u.outcome === "updated") {
      box.className = "up-outcome up-good";
      box.textContent = `Updated to ${u.outcomeVersion}. This is the new build.`;
    } else {
      // A failed update is LOUD. The old build still runs, so nothing looks wrong - which is
      // exactly why it has to say so, next to the file that holds the reason.
      box.className = "up-outcome up-bad";
      box.textContent =
        `The last update did not apply — this is still the old build. ${u.log} says why.`;
    }
  }

  buttons(u) {
    const offer = u.verdict === "Available";
    const busy = u.step === "Downloading" || u.step === "Extracting";

    $("up-download").hidden = !offer || busy || u.step === "Ready";
    $("up-skip").hidden = !offer || busy || u.step === "Ready";
    $("up-install").hidden = u.step !== "Ready";
    $("up-check").disabled = u.busy;

    if (offer && u.releaseSize > 0) {
      $("up-download").textContent = `Download (${mb(u.releaseSize)})`;
    }
  }

  progress(u) {
    const showing = u.step === "Downloading" || u.step === "Extracting" || u.step === "Ready"
      || u.step === "Failed";
    $("up-progress").hidden = !showing;
    if (!showing) return;

    // A width of zero when the size is unknown, rather than an animation standing in for one.
    // "How much is left" is the question this bar exists to answer, and inventing an answer
    // is worse than admitting there is none.
    const fraction = u.total > 0 ? Math.min(1, u.received / u.total) : 0;
    $("up-bar-fill").style.width = `${(fraction * 100).toFixed(1)}%`;
    $("up-bar-fill").className = u.step === "Failed" ? "up-fill-bad" : "";

    $("up-progress-text").textContent = u.step === "Downloading" && u.total > 0
      ? `${mb(u.received)} of ${mb(u.total)} — ${u.installStatus}`
      : u.installStatus;
  }

  notes(u) {
    const has = Boolean(u.notes);
    $("up-notes-card").hidden = !has;
    if (!has) return;

    $("up-release").textContent = u.releaseTag ? `${u.releaseName} (${u.releaseTag})` : u.releaseName;

    // textContent, never innerHTML. The release body is markdown written outside this
    // repository and arrives over the network; a page that renders it as markup is a page
    // that runs whatever a release note contains.
    $("up-notes").textContent = u.notes;
  }
}
