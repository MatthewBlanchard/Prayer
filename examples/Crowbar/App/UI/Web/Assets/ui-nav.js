
  window.useMissionPrompt = function (promptText, missionId, returnPoi) {
    var lines = [];
    var text = (promptText || '').toString().trim();
    if (text.length > 0) lines.push(text);
    var mid = (missionId || '').toString().trim();
    if (mid.length > 0) lines.push('mission_id=' + mid);
    var poi = (returnPoi || '').toString().trim();
    if (poi.length > 0) lines.push('return_poi=' + poi);
    if (lines.length === 0) return;
    var finalPrompt = lines.join('\n');
    var promptInput = document.querySelector("#prompt-form textarea[name='prompt']");
    if (!promptInput) return;
    promptInput.value = finalPrompt;
    promptInput.focus();
  };

  window.closeAllSidebarLayers = function () {
    document.querySelectorAll('.panel-layer.open').forEach(function (layer) {
      layer.classList.remove('open');
    });
  };

  window.openSidebarLayer = function (id) {
    window.closeAllSidebarLayers();
    var layer = document.getElementById(id);
    if (layer) layer.classList.add('open');
  };

  window.applyMapSubtabSelection = function () {
    var page = document.querySelector('#state-pane-map .map-page');
    if (!page) return;
    var targetTab = (window._mapSubtabSelection || 'system').toString().trim().toLowerCase();
    if (targetTab !== 'galaxy') targetTab = 'system';

    page.querySelectorAll('.map-subtab-btn').forEach(function (btn) {
      var tab = (btn.getAttribute('data-map-tab') || '').trim().toLowerCase();
      var active = tab === targetTab;
      btn.classList.toggle('active', active);
      btn.setAttribute('aria-selected', active ? 'true' : 'false');
    });
    page.querySelectorAll('.map-subtab-pane').forEach(function (pane) {
      var paneTab = (pane.getAttribute('data-map-pane') || '').trim().toLowerCase();
      pane.classList.toggle('active', paneTab === targetTab);
    });
  };

  function activateStateTab(tabBtn, focusAfter) {
    if (!tabBtn) return;
    var panel = document.getElementById('state-panel');
    if (!panel) return;

    var tab = tabBtn.getAttribute('data-tab');
    if (!tab) return;

    panel.querySelectorAll("[role='tab']").forEach(function (b) {
      var selected = (b === tabBtn);
      b.setAttribute('aria-selected', selected ? 'true' : 'false');
      b.setAttribute('tabindex', selected ? '0' : '-1');
      b.classList.toggle('active', selected);
    });

    panel.querySelectorAll('.tab-pane').forEach(function (p) {
      p.classList.remove('active');
      p.setAttribute('hidden', '');
    });

    var target = document.getElementById('state-pane-' + tab);
    if (target) {
      target.classList.add('active');
      target.removeAttribute('hidden');
      htmx.trigger(target, 'load');
    }

    if (tab === 'map') {
      window.applyMapSubtabSelection();
      window.renderGalaxyMapCanvases();
    }

    if (focusAfter) tabBtn.focus();
  }

  function activateRightTab(tabBtn, focusAfter) {
    if (!tabBtn) return;
    var tab = (tabBtn.getAttribute('data-right-tab') || '').trim().toLowerCase();
    if (!tab) return;

    var tabList = tabBtn.closest("[role='tablist']");
    if (!tabList) return;
    var panel = tabList.parentElement;
    if (!panel) return;

    tabList.querySelectorAll("[role='tab'].tab-btn[data-right-tab]").forEach(function (b) {
      var selected = (b === tabBtn);
      b.setAttribute('aria-selected', selected ? 'true' : 'false');
      b.setAttribute('tabindex', selected ? '0' : '-1');
      b.classList.toggle('active', selected);
    });

    panel.querySelectorAll(".tab-pane[id^='right-pane-']").forEach(function (p) {
      p.classList.remove('active');
      p.setAttribute('hidden', '');
    });

    var target = document.getElementById('right-pane-' + tab);
    if (target) {
      target.classList.add('active');
      target.removeAttribute('hidden');
    }

    if (focusAfter) tabBtn.focus();
  }

  function activateTabButton(tabBtn, focusAfter) {
    if (!tabBtn) return;
    if (tabBtn.hasAttribute('data-tab')) {
      activateStateTab(tabBtn, focusAfter);
      return;
    }
    if (tabBtn.hasAttribute('data-right-tab')) {
      activateRightTab(tabBtn, focusAfter);
    }
  }

  window.openStateTab = function (tabId) {
    var panel = document.getElementById('state-panel');
    if (!panel) return;
    var normalized = (tabId || '').toString().trim().toLowerCase();
    if (!normalized) return;
    var btn = panel.querySelector("[role='tab'].tab-btn[data-tab='" + normalized + "']");
    if (!btn) return;
    activateStateTab(btn, false);
  };

  document.addEventListener('click', function (e) {
    var btn = e.target.closest("[role='tab'].tab-btn");
    if (btn) { activateTabButton(btn, false); return; }

    var mapTabBtn = e.target.closest('.map-subtab-btn');
    if (mapTabBtn) {
      var page = mapTabBtn.closest('.map-page');
      if (!page) return;
      var targetTab = (mapTabBtn.getAttribute('data-map-tab') || '').trim();
      if (!targetTab) return;
      window._mapSubtabSelection = targetTab.toLowerCase();

      page.querySelectorAll('.map-subtab-btn').forEach(function (b) {
        var active = b === mapTabBtn;
        b.classList.toggle('active', active);
        b.setAttribute('aria-selected', active ? 'true' : 'false');
      });
      page.querySelectorAll('.map-subtab-pane').forEach(function (pane) {
        var paneTab = (pane.getAttribute('data-map-pane') || '').trim();
        pane.classList.toggle('active', paneTab === targetTab);
      });
      window.renderGalaxyMapCanvases();
      return;
    }

    var mapActionBtn = e.target.closest(".map-center-btn[data-map-action='center-current']");
    if (mapActionBtn) {
      var mapPage = mapActionBtn.closest('.map-page');
      if (!mapPage) return;
      var canvas = mapPage.querySelector(".map-subtab-pane.active .galaxy-map-canvas[data-map-mode='galaxy']");
      if (canvas) window.centerGalaxyMapOnCurrent(canvas);
      return;
    }

    var promptBtn = e.target.closest('.mission-use-prompt');
    if (promptBtn) {
      window.useMissionPrompt(
        promptBtn.getAttribute('data-mission-prompt') || '',
        promptBtn.getAttribute('data-mission-id') || '',
        promptBtn.getAttribute('data-return-poi') || '');
      return;
    }

    var addBtn = e.target.closest('#open-add-bot');
    if (addBtn) { window.openSidebarLayer('add-bot-panel-layer'); return; }

    var llmBtn = e.target.closest('#open-llm-settings');
    if (llmBtn) { window.openSidebarLayer('llm-panel-layer'); return; }

    var closeBtn = e.target.closest('[data-close-layer]');
    if (closeBtn) { window.closeAllSidebarLayers(); return; }

    var openLayer = e.target.closest('.panel-layer.open');
    if (openLayer && e.target === openLayer) window.closeAllSidebarLayers();
  });

  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') { window.closeAllSidebarLayers(); return; }

    var currentTab = e.target.closest("[role='tab'].tab-btn");
    if (!currentTab) return;
    if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(e.key)) return;

    var tabList = currentTab.closest("[role='tablist']");
    if (!tabList) return;

    var tabs = Array.prototype.slice.call(tabList.querySelectorAll("[role='tab'].tab-btn"));
    if (tabs.length === 0) return;

    var index = tabs.indexOf(currentTab);
    if (index < 0) return;

    var nextIndex = index;
    if (e.key === 'ArrowRight') nextIndex = (index + 1) % tabs.length;
    else if (e.key === 'ArrowLeft') nextIndex = (index - 1 + tabs.length) % tabs.length;
    else if (e.key === 'Home') nextIndex = 0;
    else if (e.key === 'End') nextIndex = tabs.length - 1;

    e.preventDefault();
    activateTabButton(tabs[nextIndex], true);
  });

  // Expose activateStateTab for use in ui-state.js (htmx:afterSwap handler).
  window._activateStateTab = activateStateTab;
