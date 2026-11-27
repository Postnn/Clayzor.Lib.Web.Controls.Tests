import assert from "node:assert/strict";
import test from "node:test";
import { update, detach } from "../../src/Clayzor.Lib.Web.Controls/wwwroot/js/clayFieldChange.js";

const observers = new Map();
globalThis.MutationObserver = class {
    constructor(callback) { this.callback = callback; }
    observe(root) { this.root = root; observers.set(root, this.callback); }
    disconnect() { observers.delete(this.root); }
};
const raw = "data-clay-field-raw-changed";
const typed = "data-clay-field-changed";
const restored = "data-clay-field-restored";

function fixture(value, type = "text") {
    const description = { id: "clay-field-raw-a", textContent: "" };
    const listeners = new Map();
    const attributes = new Map();
    const root = {
        isConnected: true,
        addEventListener: (name, listener) => listeners.set(name, listener),
        removeEventListener: name => listeners.delete(name),
        toggleAttribute: (key, value) => value ? attributes.set(key, "") : attributes.delete(key),
        hasAttribute: key => attributes.has(key),
        setAttribute: (key, value) => attributes.set(key, value),
        getAttribute: key => attributes.get(key) ?? null,
        removeAttribute: key => attributes.delete(key),
        querySelector: () => description,
        querySelectorAll: () => input.hasAttribute(restored) ? [input] : [],
    };
    const inputAttrs = new Map();
    const input = {
        value, type, isConnected: true,
        matches: () => true, closest: () => root,
        getAttribute: key => inputAttrs.get(key) ?? null,
        hasAttribute: key => inputAttrs.has(key),
        removeAttribute: key => inputAttrs.delete(key),
    };
    return { root, input, description, inputAttrs,
        emit: (type = "input", isTrusted = true) => listeners.get(type)?.({ type, isTrusted, target: input }),
        render: () => observers.get(root)?.()
    };
}

test("accepted baseline leaves late input marked and exact revert removes it", () => {
    const f = fixture("initial\nline");
    update(f.root, "initial\r\nline", false);
    f.emit();
    assert.equal(f.root.hasAttribute(raw), false);
    f.input.value = "submitted";
    f.emit();
    assert.equal(f.root.hasAttribute(raw), true);
    assert.equal(f.root.getAttribute("aria-describedby"), f.description.id);
    f.input.value = "later";
    f.emit();
    update(f.root, "submitted", false);
    assert.equal(f.root.hasAttribute(raw), true);
    f.input.value = "submitted";
    f.emit();
    assert.equal(f.root.hasAttribute(raw), false);
    assert.equal(f.root.getAttribute("aria-describedby"), null);
    detach(f.root);
});

test("lookup caption loading does not mark input while an unresolved query does", () => {
    const f = fixture("#8");
    f.inputAttrs.set("data-clay-synced-text", "#8");
    update(f.root, null, true);
    f.inputAttrs.set("data-clay-synced-text", "resolved");
    f.input.value = "resolved";
    f.render();
    assert.equal(f.root.hasAttribute(raw), false);
    f.input.value = "search";
    f.emit();
    assert.equal(f.root.hasAttribute(raw), true);
    f.root.setAttribute(typed, "");
    f.root.setAttribute("aria-describedby", "typed-description");
    f.render();
    assert.equal(f.description.textContent, "");
    assert.equal(f.root.getAttribute("aria-describedby"), "typed-description");
    f.inputAttrs.set("data-clay-synced-text", "search");
    f.render();
    assert.equal(f.root.hasAttribute(raw), false);
    assert.equal(f.root.hasAttribute(typed), true);
    detach(f.root);
});

test("restored raw text is marked without treating unrelated synthetic events as user edits", () => {
    const f = fixture("restored");
    f.inputAttrs.set(restored, "");
    update(f.root, "initial", false);
    assert.equal(f.root.hasAttribute(raw), true);
    assert.equal(f.input.hasAttribute(restored), false);
    f.input.value = "reset";
    update(f.root, "reset", false);
    assert.equal(f.root.hasAttribute(raw), false);
    f.input.value = "synthetic";
    f.emit("input", false);
    assert.equal(f.root.hasAttribute(raw), false);
    f.emit("clay:field-restored", false);
    assert.equal(f.root.hasAttribute(raw), true);
    detach(f.root);
    assert.equal(f.root.hasAttribute(raw), false);
});

test("password fields never publish a raw marker even if an outer wrapper is configured", () => {
    const f = fixture("edited", "password");
    update(f.root, "", false);
    f.emit();
    assert.equal(f.root.hasAttribute(raw), false);
    assert.equal(f.description.textContent, "");
    detach(f.root);
});
