"use strict";

const stylesheet = { files: ["selection.css"], origin: "AUTHOR" };
// Serialize clicks and document arrivals so fast toggles cannot leave stale CSS.
let pending = Promise.resolve();
function enqueue(work) {
  const result = pending.then(work);
  pending = result.catch(error => console.warn("Invisible Selection:", error.message));
  return result;
}

async function isEnabled() {
  const { enabled = true } = await chrome.storage.local.get("enabled");
  return enabled;
}

async function updateBadge(enabled) {
  await Promise.all([
    chrome.action.setBadgeText({ text: enabled ? "ON" : "OFF" }),
    chrome.action.setBadgeBackgroundColor({ color: enabled ? "#237a57" : "#60656c" }),
    chrome.action.setTitle({ title: `Invisible Selection: ${enabled ? "ON — click to turn off" : "OFF — click to turn on"}` })
  ]);
}

async function apply(target, enabled) {
  try {
    // Remove first: repeated initialization must not accumulate stylesheets.
    await chrome.scripting.removeCSS({ ...stylesheet, target });
    if (enabled) await chrome.scripting.insertCSS({ ...stylesheet, target });
  } catch {
    // Browser settings, stores, PDFs, closed/navigating tabs and other protected
    // documents reject injection. Continue applying to the other tabs.
  }
}

async function refreshTabs(enabled) {
  const tabs = await chrome.tabs.query({});
  await Promise.all(tabs.filter(tab => Number.isInteger(tab.id)).map(tab =>
    apply({ tabId: tab.id, allFrames: true }, enabled)));
  await updateBadge(enabled);
}

async function toggle() {
  const enabled = !(await isEnabled());
  await chrome.storage.local.set({ enabled });
  await refreshTabs(enabled);
}

chrome.action.onClicked.addListener(() => { enqueue(toggle); });
chrome.runtime.onInstalled.addListener(() => {
  enqueue(async () => refreshTabs(await isEnabled()));
});
chrome.runtime.onStartup.addListener(() => {
  enqueue(async () => refreshTabs(await isEnabled()));
});
chrome.runtime.onMessage.addListener((message, sender, respond) => {
  if (message?.type !== "selection-document-ready" || !sender.tab || !sender.documentId) return;
  enqueue(async () => {
    await apply({ tabId: sender.tab.id, documentIds: [sender.documentId] }, await isEnabled());
    respond({ ok: true });
  }).catch(() => respond({ ok: false }));
  return true;
});

enqueue(async () => updateBadge(await isEnabled()));
