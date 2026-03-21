
  window.applyMapSubtabSelection();
  window.renderGalaxyMapCanvases();
  if (window.ensureGalaxyMapDataPolling) window.ensureGalaxyMapDataPolling();
  if (window.ensureTickStatusPolling) window.ensureTickStatusPolling();
  window.refreshTickStatusBar();
  startTickBarAnimationLoop();
  window.ensureScriptEditor();
}());
