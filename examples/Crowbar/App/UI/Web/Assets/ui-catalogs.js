
  window.filterCatalogEntries = function (query) {
    var q = (query || '').toLowerCase().trim();
    document.querySelectorAll('#state-pane-catalog .catalog-entry').forEach(function (row) {
      var hay = row.getAttribute('data-search') || '';
      row.style.display = q === '' || hay.indexOf(q) >= 0 ? '' : 'none';
    });
  };

  window._tradeCatalogQuery = window._tradeCatalogQuery || '';
  if (typeof window._tradeOnlyWithOrders !== 'boolean') window._tradeOnlyWithOrders = true;

  window.toggleTradeOnlyWithOrders = function (enabled) {
    window._tradeOnlyWithOrders = !!enabled;
    window.filterTradeCatalogItems(window._tradeCatalogQuery || '');
  };

  window.filterTradeCatalogItems = function (query) {
    var next = (typeof query === 'string' ? query : window._tradeCatalogQuery || '');
    var q = next.toLowerCase().trim();
    window._tradeCatalogQuery = next;

    var pane = document.getElementById('state-pane-trade');
    if (!pane) return;

    pane.querySelectorAll('.trade-catalog-entry').forEach(function (row) {
      var hay = row.getAttribute('data-search') || '';
      var hasOrders = (row.getAttribute('data-has-orders') || '') === 'true';
      var matchesSearch = q === '' || hay.indexOf(q) >= 0;
      var matchesOrders = !window._tradeOnlyWithOrders || hasOrders;
      row.style.display = matchesSearch && matchesOrders ? '' : 'none';
    });

    Array.prototype.slice.call(pane.querySelectorAll('details.trade-item-category'))
      .reverse()
      .forEach(function (group) {
        var hasVisible = Array.prototype.some.call(
          group.querySelectorAll('.trade-catalog-entry'),
          function (row) { return row.style.display !== 'none'; });
        group.style.display = hasVisible ? '' : 'none';
      });

    var input = pane.querySelector("input.catalog-search[oninput*='filterTradeCatalogItems']");
    if (input && input.value !== next) input.value = next;
    var toggle = pane.querySelector('.trade-only-orders-toggle input[type="checkbox"]');
    if (toggle && toggle.checked !== !!window._tradeOnlyWithOrders) {
      toggle.checked = !!window._tradeOnlyWithOrders;
    }
  };

  window.submitTradeBuy = function (_event, form) {
    if (!form) return false;
    var itemId = (form.getAttribute('data-item-id') || '').trim();
    if (!itemId) return false;
    var qtyInput = form.querySelector("input[name='qty']");
    var qty = parseInt((qtyInput && qtyInput.value) ? qtyInput.value : '1', 10);
    if (!Number.isFinite(qty) || qty < 1) qty = 1;
    if (qtyInput) qtyInput.value = String(qty);
    var scriptInput = form.querySelector("input[name='script']");
    if (scriptInput) scriptInput.value = 'buy ' + itemId + ' ' + qty + ';';
    return true;
  };

  window._shipCatalogQuery = window._shipCatalogQuery || '';

  window.filterShipCatalogEntries = function (query) {
    var next = (typeof query === 'string' ? query : window._shipCatalogQuery || '');
    var q = next.toLowerCase().trim();
    window._shipCatalogQuery = next;

    var pane = document.getElementById('state-pane-shipyard');
    if (!pane) return;

    pane.querySelectorAll('.shipyard-ship-detail').forEach(function (row) {
      var hay = row.getAttribute('data-search') || '';
      row.style.display = q === '' || hay.indexOf(q) >= 0 ? '' : 'none';
    });

    Array.prototype.slice.call(pane.querySelectorAll('details.shipyard-faction-group'))
      .reverse()
      .forEach(function (group) {
        var hasVisible = Array.prototype.some.call(
          group.querySelectorAll('.shipyard-ship-detail'),
          function (row) { return row.style.display !== 'none'; });
        group.style.display = hasVisible ? '' : 'none';
      });

    var input = pane.querySelector("input.catalog-search[oninput*='filterShipCatalogEntries']");
    if (input && input.value !== next) input.value = next;
  };

  window._craftingQuery = window._craftingQuery || '';
  if (typeof window._craftingOnlyWithSkills !== 'boolean') window._craftingOnlyWithSkills = true;

  window.toggleCraftingOnlyWithSkills = function (enabled) {
    window._craftingOnlyWithSkills = !!enabled;
    window.filterCraftingRecipes(window._craftingQuery || '');
  };

  window.filterCraftingRecipes = function (query) {
    var next = (typeof query === 'string' ? query : window._craftingQuery || '');
    var q = next.toLowerCase().trim();
    window._craftingQuery = next;

    var pane = document.getElementById('state-pane-crafting');
    if (!pane) return;

    pane.querySelectorAll('.cargo-row').forEach(function (row) {
      var hay = row.getAttribute('data-search') || '';
      var meetsSkills = (row.getAttribute('data-meets-skill') || '') === 'true';
      var matchesSearch = q === '' || hay.indexOf(q) >= 0;
      var matchesSkills = !window._craftingOnlyWithSkills || meetsSkills;
      row.style.display = matchesSearch && matchesSkills ? '' : 'none';
    });

    Array.prototype.slice.call(pane.querySelectorAll('details.catalog-group'))
      .reverse()
      .forEach(function (group) {
        var hasVisible = Array.prototype.some.call(
          group.querySelectorAll('.cargo-row'),
          function (row) { return row.style.display !== 'none'; });
        group.style.display = hasVisible ? '' : 'none';
      });

    var input = pane.querySelector("input.catalog-search[oninput*='filterCraftingRecipes']");
    if (input && input.value !== next) input.value = next;
    var toggle = pane.querySelector('.trade-only-orders-toggle input[type="checkbox"]');
    if (toggle && toggle.checked !== !!window._craftingOnlyWithSkills) {
      toggle.checked = !!window._craftingOnlyWithSkills;
    }
  };

  window.submitCraft = function (_event, form) {
    if (!form) return false;
    var recipeId = (form.getAttribute('data-item-id') || '').trim();
    if (!recipeId) return false;
    var qtyInput = form.querySelector("input[name='qty']");
    var qty = parseInt((qtyInput && qtyInput.value) ? qtyInput.value : '1', 10);
    if (!Number.isFinite(qty) || qty < 1) qty = 1;
    if (qty > 10) qty = 10;
    if (qtyInput) qtyInput.value = String(qty);
    var scriptInput = form.querySelector("input[name='script']");
    if (scriptInput) scriptInput.value = 'craft ' + recipeId + ' ' + qty + ';';
    return true;
  };
