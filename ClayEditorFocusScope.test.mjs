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

    getClientRects() { return this.visibility === "collapsed" ? [] : [{}]; }
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
    querySelectorAll: selector => document.body.querySelectorAll(selector)
};
globalThis.getComputedStyle = element => ({ visibility: element.visibility });
globalThis.requestAnimationFrame = callback => {
    frames.set(++frameId, callback);
    return frameId;
};
globalThis.cancelAnimationFrame = id => frames.delete(id);

const { update, detach } = await import("../../src/Clayzor.Lib.Web.Controls/wwwroot/js/clayEditorFocus.js");

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
    const nativeGroup = new Element("div", scope, { "data-clay-enter-native": "" });
    const nativeInput = new Element("input", nativeGroup);
    const notes = new Element("textarea", scope);
    const last = new Element("input", scope);
    update(scope, true, true);
    nextFrame();
    assert.equal(document.activeElement, start);
    for (const gesture of [{ isComposing: true }, { keyCode: 229 }, { shiftKey: true }, { key: "Tab" }])
        assert.equal(press(start, gesture).defaultPrevented, false);
    assert.equal(press(start).defaultPrevented, true);
    assert.equal(document.activeElement, nativeInput);
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

console.log("Clay editor focus: 5 fake DOM scenarios passed");
