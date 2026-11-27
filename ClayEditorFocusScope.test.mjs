import assert from "node:assert/strict";

const frames = new Map();
const documentEvents = new Map();
let frameId = 0;

class Element {
    constructor(tag, parent = null, attributes = {}, options = {}) {
        this.tagName = tag.toUpperCase();
        this.parent = parent;
        this.children = [];
        this.attributes = attributes;
        this.events = new Map();
        this.isConnected = true;
        this.type = options.type ?? "text";
        this.tabIndex = options.tabIndex ?? 0;
        this.visibility = options.visibility ?? "visible";
        this.focusCount = 0;
        parent?.children.push(this);
    }

    hasAttribute(name) { return Object.hasOwn(this.attributes, name); }
    get parentElement() { return this.parent; }
    getAttribute(name) { return this.attributes[name] ?? null; }
    contains(element) { return element === this || this.children.some(child => child.contains(element)); }

    matches(selector) {
        return selector.split(", ").some(part => {
            if (part === ":disabled")
                return this.hasAttribute("disabled") || Boolean(this.parent?.closest("fieldset[disabled]"));
            if (part === "fieldset[disabled]")
                return this.tagName === "FIELDSET" && this.hasAttribute("disabled");
            if (part.startsWith("."))
                return this.attributes.class === part.slice(1);
            if (part.startsWith("[")) {
                const [, name, value] = part.match(/^\[([\w-]+)(?:="([^"]+)")?\]$/);
                return value === undefined ? this.hasAttribute(name) : this.attributes[name] === value;
            }
            return this.tagName === part.toUpperCase();
        });
    }

    closest(selector) {
        return this.matches(selector) ? this : this.parent?.closest(selector) ?? null;
    }

    querySelectorAll(selector) {
        return this.children.flatMap(child => [
            ...(child.matches(selector) ? [child] : []),
            ...child.querySelectorAll(selector)
        ]);
    }
    querySelector(selector) { return this.querySelectorAll(selector)[0] ?? null; }

    getClientRects() { return this.visibility === "collapsed" ? [] : [{}]; }
    dispatchEvent(event) { this.events.get(event.type)?.(event); }
    addEventListener(name, handler) { this.events.set(name, handler); }
    removeEventListener(name, handler) {
        assert.equal(this.events.get(name), handler);
        this.events.delete(name);
    }
    focus() {
        this.focusCount++;
        document.activeElement = this;
        documentEvents.get("focusin")();
    }
}

globalThis.document = {
    addEventListener: (name, handler) => documentEvents.set(name, handler),
    querySelectorAll: selector => document.body.querySelectorAll(selector),
    getElementById: id => document.body.querySelectorAll("[id]").find(element => element.getAttribute("id") === id),
    hasFocus: () => true
};
globalThis.getComputedStyle = element => ({ visibility: element.visibility });
globalThis.requestAnimationFrame = callback => {
    frames.set(++frameId, callback);
    return frameId;
};
globalThis.cancelAnimationFrame = id => frames.delete(id);

const { update, detach, claimNativeSelectionAdvance, completeNativeSelectionAdvance, claimTextEntry, completeTextEntry,
    captureDialogReturn, restoreDialogReturn, claimPopupSelection, completePopupSelection } =
    await import("../../src/Clayzor.Lib.Web.Controls/wwwroot/js/clayEditorFocus.js");

function nextFrame() {
    const pending = [...frames.values()];
    frames.clear();
    pending.forEach(callback => callback());
}

function freshScope(parent = null) {
    if (!parent) {
        document.body = new Element("body");
        document.activeElement = document.body;
        parent = document.body;
    }
    return new Element("div", parent, { "data-clay-edit-scope": "" }, { tabIndex: -1 });
}

function press(target, overrides = {}) {
    const event = {
        target, key: "Enter", defaultPrevented: false, stopped: false,
        preventDefault() { this.defaultPrevented = true; },
        stopPropagation() { this.stopped = true; },
        ...overrides
    };
    for (let node = target; node && !event.stopped; node = node.parent)
        node.events.get("keydown")?.(event);
    return event;
}

{
    const scope = freshScope();
    new Element("input", new Element("div", scope, { "data-clay-editor-exclude": "" }));
    new Element("input", scope, { readonly: "" });
    new Element("input", new Element("fieldset", scope, { disabled: "" }));
    new Element("input", new Element("div", scope, { hidden: "" }));
    new Element("input", new Element("div", scope, { inert: "" }));
    new Element("input", scope, {}, { tabIndex: -1 });
    new Element("input", scope, {}, { type: "hidden" });
    new Element("input", scope, {}, { visibility: "hidden" });
    new Element("input", scope).isConnected = false;
    const first = new Element("input", scope, { readonly: "", "data-clay-focus-editor": "" });
    const second = new Element("textarea", scope);
    update(scope, false, true);
    nextFrame();
    assert.equal(document.activeElement, document.body);
    update(scope, true, true);
    nextFrame();
    assert.equal(document.activeElement, first);
    second.focus();
    update(scope, true, true);
    nextFrame();
    assert.equal(document.activeElement, second);
    assert.equal(first.focusCount, 1);
    detach(scope);
}

{
    const scope = freshScope();
    const first = new Element("input", scope);
    const outside = new Element("button", document.body);
    update(scope, false, true);
    outside.focus();
    document.activeElement = document.body;
    update(scope, true, true);
    nextFrame();
    assert.equal(first.focusCount, 0);
    assert.equal(document.activeElement, document.body);
    detach(scope);
}

{
    const scope = freshScope();
    const outer = new Element("input", scope);
    const modal = new Element("div", scope, { class: "mud-dialog-container" });
    const inner = freshScope(modal);
    const child = new Element("input", inner);
    update(scope, true, true);
    update(inner, true, true);
    nextFrame();
    assert.equal(document.activeElement, child);
    assert.equal(outer.focusCount, 0);
    assert.equal(press(child).defaultPrevented, true);
    assert.equal(document.activeElement, child);
    detach(inner);
    detach(scope);
}

{
    const scope = freshScope();
    const start = new Element("input", scope);
    const nested = freshScope(scope);
    new Element("input", nested);
    const tableField = new Element("input", new Element("div", scope, { "data-clay-editor-exclude": "" }));
    const nativeGroup = new Element("div", scope, { "data-clay-enter-native": "" });
    const nativeInput = new Element("input", nativeGroup);
    const notes = new Element("textarea", scope);
    const last = new Element("input", scope);
    update(scope, true, true);
    nextFrame();
    assert.equal(document.activeElement, start);
    for (const gesture of [{ isComposing: true }, { keyCode: 229 }, { shiftKey: true }, { key: "Tab" }])
        assert.equal(press(start, gesture).defaultPrevented, false);
    start.attributes["aria-invalid"] = "true";
    assert.equal(press(start).defaultPrevented, true);
    assert.equal(document.activeElement, start);
    delete start.attributes["aria-invalid"];
    assert.equal(press(start).defaultPrevented, true);
    assert.equal(document.activeElement, nativeInput);
    assert.equal(press(tableField).defaultPrevented, false);
    assert.equal(press(nativeInput).defaultPrevented, false);
    assert.equal(press(notes).defaultPrevented, false);
    last.focus();
    assert.equal(press(last, { repeat: true }).defaultPrevented, true);
    assert.equal(press(last).defaultPrevented, true);
    assert.equal(document.activeElement, last);
    detach(scope);
    assert.equal(press(last).defaultPrevented, false);
}

{
    const scope = freshScope();
    update(scope, true, false);
    nextFrame();
    assert.equal(document.activeElement, scope);
    detach(scope);

    const detachedScope = freshScope();
    const first = new Element("input", detachedScope);
    update(detachedScope, true, true);
    detach(detachedScope);
    nextFrame();
    assert.equal(first.focusCount, 0);
}

{
    const scope = freshScope();
    const picker = new Element("input", scope, {
        readonly: "", role: "combobox", "data-clay-fixed-picker": "type",
        "aria-expanded": "true", "aria-controls": "own-list", "aria-activedescendant": "picked",
    });
    const next = new Element("textarea", scope);
    const menu = new Element("div", document.body, { id: "own-list", role: "listbox" });
    const item = new Element("div", menu, { role: "option", id: "picked" });
    const otherMenu = new Element("div", document.body, { id: "other-list", role: "listbox" });
    const otherItem = new Element("div", otherMenu, { role: "option" });
    update(scope, true, true);
    nextFrame();
    assert.equal(document.activeElement, picker);
    assert.equal(claimNativeSelectionAdvance("type"), null);
    const key = (value, modifiers = {}) => documentEvents.get("keydown")({ target: picker, key: value, isTrusted: true, ...modifiers });
    key("ArrowDown");
    assert.equal(claimNativeSelectionAdvance("type"), null);
    key("Escape");
    assert.equal(claimNativeSelectionAdvance("type"), null);
    key("Enter", { isComposing: true });
    assert.equal(claimNativeSelectionAdvance("type"), null);
    picker.attributes["aria-expanded"] = "false";
    key("Enter");
    assert.equal(claimNativeSelectionAdvance("type"), null);
    picker.attributes["aria-expanded"] = "true";
    key("Enter");
    assert.equal(claimNativeSelectionAdvance("type", true), null);
    const lease = claimNativeSelectionAdvance("type");
    assert.ok(lease);
    assert.equal(claimNativeSelectionAdvance("type"), null);
    assert.equal(completeNativeSelectionAdvance("type", lease), true);
    assert.equal(completeNativeSelectionAdvance("type", lease), false);
    assert.equal(document.activeElement, next);

    picker.focus();
    item.attributes["aria-selected"] = "true";
    key("Enter");
    const unchanged = claimNativeSelectionAdvance("type", true);
    assert.ok(unchanged);
    assert.equal(completeNativeSelectionAdvance("type", unchanged), true);
    assert.equal(document.activeElement, next);
    delete item.attributes["aria-selected"];

    picker.focus();
    const click = (target, isTrusted = true) => documentEvents.get("click")({ target, isTrusted });
    click(otherItem);
    click(item, false);
    assert.equal(claimNativeSelectionAdvance("type"), null);
    document.activeElement = document.body;
    click(item);
    const pointerLease = claimNativeSelectionAdvance("type");
    assert.ok(pointerLease);
    assert.equal(completeNativeSelectionAdvance("type", pointerLease), true);
    assert.equal(document.activeElement, next);

    picker.focus();
    key("Enter");
    const stale = claimNativeSelectionAdvance("type");
    otherItem.focus();
    assert.equal(completeNativeSelectionAdvance("type", stale), false);
    assert.equal(document.activeElement, otherItem);
    detach(scope);
}

{
    const scope = freshScope();
    const field = new Element("input", scope, { "data-clay-text-commit": "number-text", "data-clay-text-deferred": "" });
    const next = new Element("input", scope);
    field.value = "101";
    let changes = 0;
    field.addEventListener("change", () => { changes++; });
    update(scope, true, true);
    nextFrame();
    const enter = press(field, { isTrusted: true });
    assert.equal(enter.defaultPrevented, true);
    assert.equal(enter.stopped, false);
    assert.equal(changes, 1);
    assert.equal(document.activeElement, field);
    const entry = claimTextEntry("number-text");
    assert.equal(entry.value, "101");
    assert.equal(claimTextEntry("number-text"), null);
    field.value = "102";
    documentEvents.get("input")();
    assert.equal(completeTextEntry("number-text", entry.id), false);
    assert.equal(document.activeElement, field);
    assert.equal(field.value, "102");

    press(field, { isTrusted: true });
    const fresh = claimTextEntry("number-text");
    assert.equal(fresh.value, "102");
    assert.equal(completeTextEntry("number-text", fresh.id), true);
    assert.equal(document.activeElement, next);
    detach(scope);
}

{
    const owner = freshScope();
    const input = new Element("input", owner);
    const opener = new Element("button", owner);
    update(owner, true, true);
    nextFrame();
    opener.focus();
    const request = captureDialogReturn(owner);
    const modal = new Element("div", document.body, { class: "mud-dialog-container" });
    const trap = new Element("div", modal, { class: "mud-focus-trap" });
    const dialog = freshScope(trap);
    const editor = new Element("input", dialog);
    update(dialog, false, true);
    trap.focus();
    update(dialog, true, true);
    nextFrame();
    assert.equal(document.activeElement, editor);

    restoreDialogReturn(owner, request);
    nextFrame();
    assert.equal(document.activeElement, editor);
    document.body.children = document.body.children.filter(element => element !== modal);
    document.activeElement = document.body;
    opener.isConnected = false;
    nextFrame();
    assert.equal(document.activeElement, input);

    const newer = captureDialogReturn(owner);
    document.activeElement = document.body;
    restoreDialogReturn(owner, newer);
    documentEvents.get("pointerdown")({ isTrusted: true });
    nextFrame();
    assert.equal(document.activeElement, document.body);
    detach(dialog);
    detach(owner);
}

{
    const scope = freshScope();
    const date = new Element("input", scope, {
        "data-clay-picker-key": "calendar", "data-clay-picker-kind": "date",
        "data-clay-focus-editor": "", "data-clay-enter-native": ""
    });
    const time = new Element("input", scope, {
        "data-clay-picker-key": "clock", "data-clay-picker-kind": "time",
        "data-clay-focus-editor": "", "data-clay-enter-native": ""
    });
    const next = new Element("textarea", scope);
    const datePopover = new Element("div", document.body, { class: "mud-popover-open" });
    const datePaper = new Element("div", datePopover, { "data-clay-picker-key": "calendar" });
    new Element("div", datePaper, { class: "mud-picker-calendar" });
    const month = new Element("button", datePaper, { class: "mud-picker-month" });
    const day = new Element("button", datePaper, { class: "mud-picker-calendar-day" });
    const timePopover = new Element("div", document.body, { class: "mud-popover-closed" });
    const timePaper = new Element("div", timePopover, { "data-clay-picker-key": "clock" });
    const mask = new Element("div", timePaper, { class: "mud-picker-time-clock-mask" });
    const testClick = target => documentEvents.get("click")({ target, isTrusted: true });
    const testKey = (target, key) => documentEvents.get("keydown")({ target, key, isTrusted: true });
    update(scope, true, true);
    nextFrame();
    assert.equal(document.activeElement, date);
    testClick(month);
    assert.equal(claimPopupSelection("calendar"), null);
    testClick(day);
    const selected = claimPopupSelection("calendar");
    assert.ok(selected);
    assert.equal(claimPopupSelection("calendar"), null);
    completePopupSelection("calendar", selected);
    nextFrame();
    assert.equal(document.activeElement, date);
    datePopover.attributes.class = "mud-popover-closed";
    nextFrame();
    assert.equal(document.activeElement, time);

    testKey(time, "Enter");
    assert.equal(claimPopupSelection("clock"), null);
    timePopover.attributes.class = "mud-popover-open";
    const pointer = type => documentEvents.get(type)({ target: mask, pointerId: 3, isTrusted: true, button: 0 });
    pointer("pointerdown"); pointer("pointerup");
    assert.equal(claimPopupSelection("clock"), null);
    new Element("div", mask, { class: "mud-time-picker-minute" });
    pointer("pointerdown"); pointer("pointerup");
    const minute = claimPopupSelection("clock");
    assert.ok(minute);
    testKey(time, "Escape");
    completePopupSelection("clock", minute);
    nextFrame();
    assert.equal(document.activeElement, time);

    testKey(time, "Enter");
    const entered = claimPopupSelection("clock");
    assert.ok(entered);
    timePopover.attributes.class = "mud-popover-closed";
    completePopupSelection("clock", entered);
    nextFrame();
    assert.equal(document.activeElement, next);
    detach(scope);
}

console.log("Clay editor focus: input, fixed lists, popup selection and dialog return passed");
