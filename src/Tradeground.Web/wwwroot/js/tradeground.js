// The only JavaScript in Tradeground: a few browser features C# can't reach on its own.
// Everything else is C# (Blazor), which calls these through JS interop (IJSRuntime).

window.tradeground = {
  // sessionStorage / localStorage. Storage can be blocked (private mode, site settings), so every
  // call is wrapped: the app then simply forgets things on refresh instead of crashing.
  storageGet(kind, key) {
    try {
      return window[kind].getItem(key);
    } catch {
      return null;
    }
  },

  storageSet(kind, key, value) {
    try {
      if (value === null) window[kind].removeItem(key);
      else window[kind].setItem(key, value);
    } catch {
      // Storage blocked: nothing to do.
    }
  },

  // The native <dialog> element: the browser handles focus, Escape and the backdrop.
  showDialog(dialog) {
    if (dialog && !dialog.open) dialog.showModal();
  },

  closeDialog(dialog) {
    dialog?.close();
  },

  isDialogOpen() {
    return !!document.querySelector("dialog[open]");
  },

  prefersReducedMotion() {
    return window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  },

  // Keyboard shortcuts for a page. Keys typed into fields or while a dialog is open are ignored.
  listenForKeys(dotNetPage, keys) {
    const handler = (event) => {
      if (event.ctrlKey || event.metaKey || event.altKey) return;
      if (event.target.closest?.("input, textarea, select")) return;
      if (document.querySelector("dialog[open]")) return;
      if (!keys.includes(event.key)) return;
      if (event.key !== "Enter" || !event.target.closest?.("button, a")) {
        event.preventDefault();
        dotNetPage.invokeMethodAsync("OnShortcut", event.key);
      }
    };
    document.addEventListener("keydown", handler);
    return { dispose: () => document.removeEventListener("keydown", handler) };
  },

  scrollIntoView(element) {
    element?.scrollIntoView({ behavior: "smooth", block: "start" });
  },
};
