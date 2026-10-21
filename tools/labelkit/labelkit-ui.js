// The page pieces every label tool shares, taken from the window glass labeler (tools/windowglass)
// whose feel the author settled on: icons drawn in SVG, the page's own tooltips, sliders that fill,
// buttons that ask twice, tool sections dragged between the top bar, the left side and the bottom,
// panels resized by their edges, a toast. A job loads this, then labelkit-editor.js, then its own.
"use strict";
const LabelKit = (() => {
  const $ = selector => document.querySelector(selector);

  // ---- icons: drawn in SVG, coloured by the button's text ----
  const ICON_PATHS = {
    brush: '<path d="M18.4 2.6a2 2 0 0 1 2.9 2.9L11 15.8 8.2 13z"/><path d="M8 13.5c-2 0-3.5 1.5-3.5 3.5 0 1.6-1 2.6-2.5 3 1 1 2.5 1.5 4 1.5 3 0 5-2 5-5"/>',
    box: '<rect x="4" y="4" width="16" height="16" rx="1" stroke-dasharray="3 2.5"/>',
    lasso: '<ellipse cx="12" cy="9" rx="8" ry="5.5"/><path d="M7.5 13.5c-1.5 2.5.5 4.5 2.5 4.5s2.5 2 1.5 3.5"/>',
    polygon: '<path d="M12 3l8.5 6-3 11h-11l-3-11z"/><circle cx="12" cy="3" r="1.3" fill="currentColor"/>',
    wand: '<path d="M4 20 14.5 9.5"/><path d="M15 3v3M15 13v-1M19 8h2M9 8h2M18 5l1.5-1.5M18 11l1.5 1.5M12 5l-1.5-1.5"/>',
    tile: '<rect x="4" y="4" width="16" height="16" rx="1"/><path d="M4 12h16M12 4v16"/><rect x="4.5" y="4.5" width="7" height="7" fill="currentColor" stroke="none" opacity=".45"/>',
    stamp: '<path d="M9 3h6v5l3 3v4H6v-4l3-3z"/><path d="M5 20h14"/>',
    select: '<path d="M4 8V5a1 1 0 0 1 1-1h3M16 4h3a1 1 0 0 1 1 1v3M20 16v3a1 1 0 0 1-1 1h-3M8 20H5a1 1 0 0 1-1-1v-3"/>',
    size1: '<rect x="10.5" y="10.5" width="3" height="3" fill="currentColor"/>',
    size2: '<rect x="9" y="9" width="6" height="6" fill="currentColor"/>',
    size4: '<rect x="6" y="6" width="12" height="12" fill="currentColor"/>',
    viewRed: '<rect x="4" y="4" width="16" height="16" rx="2"/><rect x="7" y="7" width="10" height="10" fill="#ff4646" stroke="none"/>',
    viewColours: '<rect x="4" y="4" width="16" height="16" rx="2"/><rect x="7" y="7" width="5" height="10" fill="#3f8cff" stroke="none"/><rect x="12" y="7" width="5" height="10" fill="#ff8a3d" stroke="none"/>',
    viewBlackWhite: '<rect x="4" y="4" width="16" height="16" rx="2" fill="#000"/><rect x="7" y="7" width="10" height="10" fill="#fff" stroke="none"/>',
    viewArt: '<rect x="3" y="5" width="18" height="14" rx="2"/><circle cx="9" cy="10" r="1.6"/><path d="M21 16l-5-5-8 8"/>',
    viewPair: '<rect x="3" y="3" width="12" height="12" rx="2"/><rect x="9" y="9" width="12" height="12" rx="2" fill="currentColor" fill-opacity=".25"/>',
    xray: '<path d="M3 7V5a2 2 0 0 1 2-2h2M17 3h2a2 2 0 0 1 2 2v2M21 17v2a2 2 0 0 1-2 2h-2M7 21H5a2 2 0 0 1-2-2v-2"/><path d="M7 12h10"/>',
    fit: '<path d="M8 3H5a2 2 0 0 0-2 2v3M21 8V5a2 2 0 0 0-2-2h-3M3 16v3a2 2 0 0 0 2 2h3M16 21h3a2 2 0 0 0 2-2v-3"/><rect x="8" y="8" width="8" height="8" rx="1"/>',
    undo: '<path d="M9 14 4 9l5-5"/><path d="M4 9h10.5a5.5 5.5 0 0 1 0 11H11"/>',
    redo: '<path d="m15 14 5-5-5-5"/><path d="M20 9H9.5a5.5 5.5 0 0 0 0 11H13"/>',
    copy: '<rect x="8" y="8" width="13" height="13" rx="2"/><path d="M16 8V5a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h3"/>',
    paste: '<rect x="6" y="4" width="12" height="17" rx="2"/><path d="M9 2h6v4H9z"/><path d="M9 12h6M9 16h4"/>',
    skipClear: '<rect x="4" y="4" width="16" height="16" rx="2" stroke-dasharray="2.5 2.5"/><path d="M5 19 19 5"/>',
    check: '<path d="m5 12.5 4.5 4.5L19 7"/>',
    noGlass: '<circle cx="12" cy="12" r="8.5"/><path d="M6 18 18 6"/>',
    reset: '<path d="M3 12a9 9 0 1 0 2.6-6.4L3 8"/><path d="M3 3v5h5"/>',
    previous: '<path d="m15 18-6-6 6-6"/>',
    next: '<path d="m9 18 6-6-6-6"/>',
    nextTodo: '<path d="M12 4v13"/><path d="m6 11 6 6 6-6"/><path d="M6 21h12"/>',
    keys: '<rect x="2.5" y="6" width="19" height="12" rx="2"/><path d="M6 10h.01M10 10h.01M14 10h.01M18 10h.01M7 14h10"/>',
    grip: '<circle cx="9" cy="6" r="1.4" fill="currentColor" stroke="none"/><circle cx="15" cy="6" r="1.4" fill="currentColor" stroke="none"/><circle cx="9" cy="12" r="1.4" fill="currentColor" stroke="none"/><circle cx="15" cy="12" r="1.4" fill="currentColor" stroke="none"/><circle cx="9" cy="18" r="1.4" fill="currentColor" stroke="none"/><circle cx="15" cy="18" r="1.4" fill="currentColor" stroke="none"/>',
    layout: '<rect x="3" y="3" width="18" height="18" rx="2"/><path d="M3 9h18M9 9v12"/>',
    takeBack: '<path d="M3 12a9 9 0 1 0 2.6-6.4L3 8"/><path d="M3 3v5h5"/>',
    search: '<circle cx="11" cy="11" r="6.5"/><path d="m20 20-4.2-4.2"/>',
    clear: '<path d="M6 6l12 12M18 6 6 18"/>',
    water: '<path d="M12 3c3.5 4.5 6 7.6 6 10.5a6 6 0 0 1-12 0C6 10.6 8.5 7.5 12 3z"/>',
    flowing: '<path d="M4 7c2.5-2 5.5 2 8 0s5.5 2 8 0M4 12c2.5-2 5.5 2 8 0s5.5 2 8 0M4 17c2.5-2 5.5 2 8 0s5.5 2 8 0"/>',
    ice: '<path d="M12 3v18M4.2 7.5l15.6 9M4.2 16.5l15.6-9"/><path d="m9.5 4.5 2.5 2 2.5-2M9.5 19.5l2.5-2 2.5 2"/>',
    lava: '<path d="M5 20c0-6 4-8 7-15 3 7 7 9 7 15z"/><path d="M9.5 16c.5-2 1.5-3 2.5-5 1 2 2 3 2.5 5"/>',
    hot: '<path d="M8 20c-2-3 2-5 0-8M12 20c-2-3 2-5 0-8M16 20c-2-3 2-5 0-8"/><path d="M5 5h14"/>',
    others: '<rect x="4" y="4" width="7" height="7" rx="1"/><rect x="13" y="4" width="7" height="7" rx="1" opacity=".5"/><rect x="4" y="13" width="7" height="7" rx="1" opacity=".5"/><rect x="13" y="13" width="7" height="7" rx="1"/>',
    sheet: '<rect x="3" y="3" width="18" height="18" rx="2"/><path d="M3 9h18M3 15h18M9 3v18M15 3v18"/>',
    map: '<path d="M9 4 3 6v14l6-2 6 2 6-2V4l-6 2z"/><path d="M9 4v14M15 6v14"/>',
  };
  function icon(name) {
    return `<svg class="icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${ICON_PATHS[name] || ""}</svg>`;
  }
  function addIcons(root = document) {
    root.querySelectorAll("[data-icon]").forEach(button => {
      if (!button.querySelector(":scope > svg.icon")) button.insertAdjacentHTML("afterbegin", icon(button.dataset.icon));
    });
  }

  // ---- the search box's own pieces: <div class="search"><span data-search-icon></span><input
  // type="search"><button class="clear"></button></div> ----
  function setupSearch(box) {
    const holder = box.closest(".search");
    const iconPlace = holder.querySelector("[data-search-icon]");
    if (iconPlace) iconPlace.outerHTML = icon("search");
    const clear = holder.querySelector(".clear");
    if (clear) {
      clear.innerHTML = icon("clear");
      clear.onclick = () => { box.value = ""; box.dispatchEvent(new Event("input")); box.focus(); };
    }
    box.addEventListener("input", () => holder.classList.toggle("filled", !!box.value));
  }

  // ---- tooltips of the page's own, never the browser's ----
  const tip = document.createElement("div"); tip.id = "tip";
  let tipTarget = null, tipTimer = null;
  function adoptTitles(root) {
    if (root.nodeType !== 1) return;
    const elements = root.hasAttribute && root.hasAttribute("title") ? [root, ...root.querySelectorAll("[title]")] : [...root.querySelectorAll("[title]")];
    for (const element of elements) { element.dataset.tip = element.getAttribute("title"); element.removeAttribute("title"); }
  }
  function hideTip() { clearTimeout(tipTimer); tipTarget = null; tip.classList.remove("show"); }
  function showTip(element) {
    const text = element.dataset.tip; if (!text) return;
    const key = element.querySelector(":scope > kbd");
    tip.textContent = text;
    if (key && !text.includes(key.textContent)) tip.insertAdjacentHTML("beforeend", `<kbd>${key.textContent}</kbd>`);
    tip.classList.remove("above");
    const box = element.getBoundingClientRect();
    tip.style.left = "0px"; tip.style.top = "0px";
    const width = tip.offsetWidth, height = tip.offsetHeight;
    let left = box.left + box.width / 2 - width / 2;
    left = Math.max(8, Math.min(window.innerWidth - width - 8, left));
    let top = box.bottom + 9;
    if (top + height > window.innerHeight - 8) { top = box.top - height - 9; tip.classList.add("above"); }
    tip.style.left = `${left}px`; tip.style.top = `${top}px`;
    tip.style.setProperty("--arrow", `${Math.max(10, Math.min(width - 10, box.left + box.width / 2 - left))}px`);
    requestAnimationFrame(() => tip.classList.add("show"));
  }
  function setupTooltips() {
    document.body.appendChild(tip);
    adoptTitles(document.body);
    new MutationObserver(changes => {
      for (const change of changes) {
        if (change.type === "attributes" && change.target.hasAttribute("title")) adoptTitles(change.target);
        for (const node of change.addedNodes || []) adoptTitles(node);
      }
    }).observe(document.body, { subtree: true, childList: true, attributes: true, attributeFilter: ["title"] });
    document.addEventListener("pointerover", event => {
      const element = event.target.closest && event.target.closest("[data-tip]");
      if (element === tipTarget) return;
      hideTip();
      if (!element) return;
      tipTarget = element;
      tipTimer = setTimeout(() => { if (tipTarget === element) showTip(element); }, 380);
    });
    ["pointerdown", "wheel", "keydown", "dragstart"].forEach(name => document.addEventListener(name, hideTip, true));
    window.addEventListener("blur", hideTip);
  }

  // ---- the slider fills to where its thumb is ----
  function paintSlider(slider) { slider.style.setProperty("--fill", `${(slider.value - slider.min) / (slider.max - slider.min) * 100}%`); }
  function setupSliders(root = document) {
    root.querySelectorAll("input[type=range]").forEach(slider => { paintSlider(slider); slider.addEventListener("input", () => paintSlider(slider)); });
  }

  // ---- press twice: the first press arms a button and puts a Cancel beside it; the second does it.
  // Cancel, Esc, another item, or six quiet seconds call it off.
  function armTwice(button, pendingText, action) {
    if (button.classList.contains("armed")) { disarmButton(button); action(); return; }
    cancelAllArmed();
    button.dataset.original = button.innerHTML;
    button.classList.add("armed", "verdict-armed");
    const symbol = button.querySelector("svg"), key = button.querySelector("kbd");
    button.innerHTML = (symbol ? symbol.outerHTML : "") + `<span>${pendingText}</span>` + (key ? key.outerHTML : "");
    const cancel = document.createElement("button");
    cancel.className = "cancel-armed";
    cancel.dataset.tip = "Cancel (Esc)";
    cancel.innerHTML = icon("clear") + "<span>Cancel</span>";
    cancel.onclick = event => { event.stopPropagation(); disarmButton(button); };
    button.after(cancel);
    button.cancelButton = cancel;
    clearTimeout(button.disarmTimer);
    button.disarmTimer = setTimeout(() => disarmButton(button), 6000);
  }
  function disarmButton(button) {
    if (!button.classList.contains("armed")) return;
    clearTimeout(button.disarmTimer);
    button.classList.remove("armed", "verdict-armed");
    if (button.dataset.original) button.innerHTML = button.dataset.original;
    if (button.cancelButton) { button.cancelButton.remove(); button.cancelButton = null; }
  }
  function cancelAllArmed() { document.querySelectorAll(".verdict-armed").forEach(disarmButton); }
  const anyArmed = () => !!document.querySelector(".verdict-armed");

  // ---- motion helpers ----
  function replay(element, className) { if (!element) return; element.classList.remove(className); void element.offsetWidth; element.classList.add(className); }

  function toast(text) {
    const box = $("#toast"); if (!box) return;
    box.textContent = text; box.classList.add("show");
    clearTimeout(box.timer); box.timer = setTimeout(() => box.classList.remove("show"), 1700);
  }

  // ---- the keys dialog ----
  function openHelp() { const help = $("#help"); help.hidden = false; requestAnimationFrame(() => help.classList.add("open")); }
  function closeHelp() {
    const help = $("#help"); help.classList.remove("open"); clearTimeout(help.closing);
    help.closing = setTimeout(() => { if (!help.classList.contains("open")) help.hidden = true; }, 230);
  }
  const helpOpen = () => !$("#help").hidden;

  // ---- sections, section sizes, panel sizes: the arrangement, remembered in this browser ----
  // options: { storagePrefix, defaultLayout: {top, left, bottom}, panelLimits: {name: [low, high]},
  //            defaultPanels: {name: width}, panelElements: {name: () => element},
  //            growsLeft: [names], onRoomChange: () => void }
  function setupLayout(options) {
    const LAYOUT_KEY = options.storagePrefix + "Layout";
    const SECTION_SIZE_KEY = options.storagePrefix + "SectionSizes";
    const PANEL_KEY = options.storagePrefix + "Panels";
    const docks = [...document.querySelectorAll(".dock")];
    const sections = [...document.querySelectorAll(".group[data-group]")];
    const roomChanged = () => requestAnimationFrame(() => { updateCompact(); if (options.onRoomChange) options.onRoomChange(); });
    let movingSection = null;

    // Moves glide: every section's place is noted, the change is made, and each section is then
    // slid (and stretched) from where it was to where it now is.
    function glide(change) {
      const before = new Map(sections.map(section => [section, section.getBoundingClientRect()]));
      change();
      for (const section of sections) {
        const from = before.get(section), to = section.getBoundingClientRect();
        if (!to.width || !from.width) continue;
        const dx = from.left - to.left, dy = from.top - to.top, sx = from.width / to.width, sy = from.height / to.height;
        if (Math.abs(dx) < 1 && Math.abs(dy) < 1 && Math.abs(sx - 1) < .01 && Math.abs(sy - 1) < .01) continue;
        section.getAnimations().filter(animation => animation.id === "glide").forEach(animation => animation.cancel());
        const animation = section.animate(
          [{ transformOrigin: "top left", transform: `translate(${dx}px, ${dy}px) scale(${sx}, ${sy})` }, { transformOrigin: "top left", transform: "none" }],
          { duration: 260, easing: "cubic-bezier(.2, .8, .2, 1)" });
        animation.id = "glide";
      }
    }
    function placeSection(dock, section, before) {
      const tail = dock.querySelector(":scope > .dock-tail");
      dock.insertBefore(section, before || tail || null);
    }
    function applyLayout(layout) {
      const placed = new Set();
      for (const dock of docks) {
        for (const key of layout[dock.dataset.dock] || []) {
          const section = sections.find(s => s.dataset.group === key);
          if (section && !placed.has(key)) { placeSection(dock, section); placed.add(key); }
        }
      }
      for (const section of sections) if (!placed.has(section.dataset.group)) placeSection(docks[0], section);
      roomChanged();
    }
    function currentLayout() {
      const layout = {};
      for (const dock of docks) layout[dock.dataset.dock] = [...dock.querySelectorAll(":scope > .group")].map(s => s.dataset.group);
      return layout;
    }
    function saveLayout() { try { localStorage.setItem(LAYOUT_KEY, JSON.stringify(currentLayout())); } catch {} }
    function savedLayout() { try { return JSON.parse(localStorage.getItem(LAYOUT_KEY)) || options.defaultLayout; } catch { return options.defaultLayout; } }
    // The section after which the pointer sits, in reading order for a row, top to bottom for a column.
    function sectionAfterPointer(dock, x, y) {
      const others = [...dock.querySelectorAll(":scope > .group")].filter(s => s !== movingSection);
      const column = getComputedStyle(dock).flexDirection === "column";
      for (const other of others) {
        const r = other.getBoundingClientRect();
        if (column ? y < r.top + r.height / 2 : (y < r.top || (y < r.bottom && x < r.left + r.width / 2))) return other;
      }
      return null;
    }
    for (const section of sections) {
      const label = section.querySelector(".label");
      label.insertAdjacentHTML("afterbegin", icon("grip"));
      label.addEventListener("mousedown", () => { section.draggable = true; });
      section.addEventListener("dragstart", event => {
        if (!section.draggable) return;
        movingSection = section;
        event.dataTransfer.effectAllowed = "move";
        event.dataTransfer.setData("text/plain", section.dataset.group);
        const ghost = document.createElement("div"); ghost.id = "dragGhost";
        ghost.style.setProperty("--tone", getComputedStyle(section).getPropertyValue("--tone"));
        ghost.innerHTML = icon("grip") + section.querySelector(".label").textContent;
        document.body.appendChild(ghost);
        event.dataTransfer.setDragImage(ghost, 14, 14);
        setTimeout(() => ghost.remove(), 0);
        requestAnimationFrame(() => { section.classList.add("moving"); document.body.classList.add("moving-section"); });
      });
      section.addEventListener("dragend", () => {
        replay(section, "settle");
        section.draggable = false; section.classList.remove("moving");
        document.body.classList.remove("moving-section");
        docks.forEach(d => d.classList.remove("over"));
        movingSection = null;
        saveLayout(); roomChanged();
      });
    }
    window.addEventListener("mouseup", () => sections.forEach(s => { if (s !== movingSection) s.draggable = false; }));
    for (const dock of docks) {
      dock.addEventListener("dragover", event => {
        if (!movingSection) return;
        event.preventDefault();
        docks.forEach(d => d.classList.toggle("over", d === dock));
        const before = sectionAfterPointer(dock, event.clientX, event.clientY);
        if (movingSection.parentElement !== dock || movingSection.nextElementSibling !== before) glide(() => placeSection(dock, movingSection, before));
      });
      dock.addEventListener("drop", event => event.preventDefault());
    }

    // section sizes: the corner handle sets one; double-click the name to fit again
    let applyingSectionSizes = false;
    function savedSectionSizes() { try { return JSON.parse(localStorage.getItem(SECTION_SIZE_KEY)) || {}; } catch { return {}; } }
    function saveSectionSizes() {
      if (applyingSectionSizes) return;
      const sizes = {};
      for (const section of sections) if (section.style.width || section.style.height) sizes[section.dataset.group] = { width: section.style.width, height: section.style.height };
      try { localStorage.setItem(SECTION_SIZE_KEY, JSON.stringify(sizes)); } catch {}
    }
    function applySectionSizes(sizes) {
      applyingSectionSizes = true;
      for (const section of sections) {
        const size = sizes[section.dataset.group];
        section.style.width = size ? size.width : ""; section.style.height = size ? size.height : "";
        section.classList.toggle("sized", !!size);
      }
      applyingSectionSizes = false;
    }
    // The browser's own corner handle writes the size inline; notice it and keep it.
    const sectionWatcher = new ResizeObserver(() => {
      for (const section of sections) {
        // never smaller than its buttons: a size that would cut one off gives way to the content
        if (section.style.height && section.scrollHeight > section.clientHeight + 1) section.style.height = "";
        if (section.style.width && section.scrollWidth > section.clientWidth + 1) section.style.width = "";
        section.classList.toggle("sized", !!(section.style.width || section.style.height));
      }
      saveSectionSizes();
    });
    for (const section of sections) {
      sectionWatcher.observe(section);
      section.querySelector(".label").addEventListener("dblclick", () => {
        section.style.width = ""; section.style.height = ""; section.classList.remove("sized");
        saveSectionSizes(); roomChanged();
      });
    }

    // panel sizes: drag a splitter
    const limits = options.panelLimits || {};
    function setPanelWidth(name, width) {
      const [low, high] = limits[name];
      document.documentElement.style.setProperty(`--${name}-width`, `${Math.round(Math.max(low, Math.min(high, width)))}px`);
    }
    function applyPanels(widths) { for (const [name, width] of Object.entries(widths)) if (limits[name]) setPanelWidth(name, width); }
    function savePanels() {
      const out = {};
      for (const name of Object.keys(limits)) {
        const value = document.documentElement.style.getPropertyValue(`--${name}-width`);
        if (value) out[name] = parseFloat(value);
      }
      try { localStorage.setItem(PANEL_KEY, JSON.stringify(out)); } catch {}
    }
    document.querySelectorAll(".splitter").forEach(splitter => {
      splitter.addEventListener("mousedown", event => {
        event.preventDefault();
        const name = splitter.dataset.panel, start = event.clientX;
        const startWidth = options.panelElements[name]().getBoundingClientRect().width;
        const direction = (options.growsLeft || []).includes(name) ? -1 : 1;
        splitter.classList.add("dragging"); document.body.classList.add("resizing");
        const move = moveEvent => { setPanelWidth(name, startWidth + direction * (moveEvent.clientX - start)); updateCompact(); if (options.onRoomChange) options.onRoomChange(); };
        const up = () => {
          window.removeEventListener("mousemove", move); window.removeEventListener("mouseup", up);
          splitter.classList.remove("dragging"); document.body.classList.remove("resizing");
          savePanels(); roomChanged();
        };
        window.addEventListener("mousemove", move); window.addEventListener("mouseup", up);
      });
    });

    // A dock shows icons only when its words do not fit: the left one when it is narrow, a bar
    // when its sections would wrap onto a second row.
    function updateCompact() {
      for (const dock of docks) {
        dock.classList.remove("compact", "no-keys");
        const groups = [...dock.querySelectorAll(":scope > .group")];
        if (!groups.length) continue;
        if (dock.dataset.dock !== "left" && groups.every(group => group.classList.contains("sized"))) continue;
        let tight;
        if (dock.dataset.dock === "left") {
          const buttons = [...dock.querySelectorAll(".group button")];
          const overflowing = () => buttons.some(button => button.scrollWidth > button.clientWidth + 1);
          if (!overflowing()) continue;
          dock.classList.add("no-keys");
          tight = overflowing();
          if (!tight) continue;
          dock.classList.remove("no-keys");
        } else {
          const wraps = () => groups.some(group => Math.abs(group.offsetTop - groups[0].offsetTop) > 8);
          if (!wraps()) continue;
          dock.classList.add("no-keys");
          tight = wraps();
          if (!tight) continue;
          dock.classList.remove("no-keys");
        }
        dock.classList.toggle("compact", tight);
      }
    }

    const reset = $("#resetLayout");
    if (reset) reset.onclick = () => {
      glide(() => applyLayout(options.defaultLayout)); saveLayout();
      for (const name of Object.keys(limits)) document.documentElement.style.removeProperty(`--${name}-width`);
      try { localStorage.removeItem(PANEL_KEY); localStorage.removeItem(SECTION_SIZE_KEY); } catch {}
      applySectionSizes({});
      applyPanels(options.defaultPanels || {});
      roomChanged();
    };

    applyLayout(savedLayout());
    applySectionSizes(savedSectionSizes());
    let savedPanels = null;
    try { savedPanels = JSON.parse(localStorage.getItem(PANEL_KEY)); } catch {}
    applyPanels(savedPanels || options.defaultPanels || {});
    // Checked again whenever a dock's room changes: the window, a panel dragged, a section moved.
    const roomWatcher = new ResizeObserver(() => requestAnimationFrame(updateCompact));
    [document.body, $("#editor"), $("main")].filter(Boolean).forEach(element => roomWatcher.observe(element));
    updateCompact();
    return { updateCompact };
  }

  // ---- the line to the server: changes pushed as they happen; a dropped line is tried again ----
  function connectLive(onEvent) {
    const source = new EventSource("/api/events");
    source.onopen = () => document.body.classList.remove("offline");
    source.onerror = () => document.body.classList.add("offline");
    source.onmessage = message => { try { onEvent(JSON.parse(message.data)); } catch (problem) { console.error(problem); } };
    return source;
  }

  async function api(path, body) {
    const response = await fetch(path, body === undefined ? undefined
      : { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
    const value = await response.json();
    if (!response.ok) throw new Error(value.error || response.statusText);
    return value;
  }

  // Everything that does not depend on the job, ready as soon as the page has its HTML.
  function start() {
    addIcons();
    setupTooltips();
    setupSliders();
    document.querySelectorAll(".search input[type=search]").forEach(setupSearch);
    const help = $("#help");
    if (help) help.addEventListener("click", event => { if (event.target === help) closeHelp(); });
  }

  return { $, ICON_PATHS, icon, addIcons, setupSearch, adoptTitles, paintSlider, setupSliders, armTwice, disarmButton,
           cancelAllArmed, anyArmed, replay, toast, openHelp, closeHelp, helpOpen, setupLayout, connectLive, api, start };
})();
