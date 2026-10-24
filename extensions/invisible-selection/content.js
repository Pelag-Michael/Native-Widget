// The worker applies CSS to this exact document, including new frames.
// No selection, clipboard, or page text is read or changed.
chrome.runtime.sendMessage({ type: "selection-document-ready" }).catch(() => {});
