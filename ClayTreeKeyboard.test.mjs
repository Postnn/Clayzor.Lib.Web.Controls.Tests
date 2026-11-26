import assert from "node:assert/strict";

class Element {
    constructor(parent, attributes = {}) {
        this.parentElement = parent;
        this.attrs = attributes;
        this.events = new Map();
        this.children = [];
        this.dataset = { clayTreeNode: attributes["data-clay-tree-node"] };
        this.isConnected = true;
        parent?.children.push(this);
    }

    getAttribute(name) { return this.attrs[name] ?? null; }
    hasAttribute(name) { return name in this.attrs; }
    matches(selector) {
        return selector.split(", ").some(part => {
            if (part[0] === ".") return this.attrs.class === part.slice(1);
            const [, name, value] = part.match(/^\[([\w-]+)(?:="([^"]+)")?\]$/);
            return value === undefined ? this.hasAttribute(name) : this.getAttribute(name) === value;
        });
    }

    closest(selector) { return this.matches(selector) ? this : this.parentElement?.closest(selector) ?? null; }
    contains(element) { return element === this || this.children.some(child => child.contains(element)); }
    getClientRects() { return this.isConnected ? [{}] : []; }
    querySelectorAll(selector) {
        return this.children.flatMap(child => [
            ...(child.matches(selector) ? [child] : []),
            ...child.querySelectorAll(selector)
        ]);
    }

    addEventListener(event, handler, { signal } = {}) {
        this.events.set(event, handler);
        signal?.addEventListener("abort", () => this.events.delete(event));
    }

    focus() {
        document.activeElement = this;
        documentEvents.get("focusin")();
        for (let node = this; node; node = node.parentElement)
            node.events.get("focusin")?.({ target: this });
    }

    remove() {
        this.parentElement.children = this.parentElement.children.filter(item => item !== this);
        this.isConnected = false;
        if (document.activeElement === this)
            document.activeElement = document.body;
    }
}

const documentEvents = new Map();
let changed;
globalThis.window = {};
globalThis.document = {
    addEventListener: (name, handler) => documentEvents.set(name, handler),
    querySelectorAll: selector => document.body.querySelectorAll(selector),
    hasFocus: () => true
};
globalThis.getComputedStyle = () => ({ visibility: "visible" });
globalThis.MutationObserver = class {
    constructor(callback) { changed = callback; }
    observe() {}
    disconnect() {}
};

await import("../../src/Clayzor.Lib.Web.Controls/wwwroot/js/clayTree.js");

function press(root, target, key, options = {}) {
    const event = {
        key, target, defaultPrevented: false,
        preventDefault() { this.defaultPrevented = true; },
        stopPropagation() {},
        ...options
    };
    root.events.get("keydown")?.(event);
    return event.defaultPrevented;
}

function node(root, id, attrs = {}) {
    return new Element(root, { role: "treeitem", "data-clay-tree-node": id, ...attrs });
}

const body = document.body = new Element(null);
document.activeElement = body;
const root = new Element(body, { role: "tree" });
const alpha = node(root, "a", { "aria-expanded": "true" });
const group = new Element(alpha, { role: "group" });
const child = node(group, "a1", { "aria-checked": "false" });
node(group, "a2", { hidden: "" });
const beta = node(root, "b", { "aria-expanded": "false", "aria-selected": "true" });
const commands = [];
window.clayTree.attachKeyboard(root, {
    async invokeMethodAsync(method, id, action) { commands.push([method, id, action]); }
});

assert.equal(document.activeElement, body);
assert.equal(beta.tabIndex, 0);
beta.focus();
assert.equal(press(root, beta, "ArrowUp"), true);
assert.equal(document.activeElement, child);
assert.equal(press(root, child, "ArrowLeft"), true);
assert.equal(document.activeElement, alpha);
assert.equal(press(root, alpha, "ArrowRight"), true);
assert.equal(document.activeElement, child);
assert.equal(press(root, child, "End"), true);
assert.equal(document.activeElement, beta);
assert.equal(press(root, beta, "Home"), true);
assert.equal(document.activeElement, alpha);
assert.equal(press(root, alpha, "ArrowLeft"), true);
await new Promise(resolve => setImmediate(resolve));
assert.deepEqual(commands.pop(), ["InvokeKeyboardActionAsync", "a", "collapse"]);
assert.equal(press(root, beta, "ArrowRight"), true);
await new Promise(resolve => setImmediate(resolve));
assert.deepEqual(commands.pop(), ["InvokeKeyboardActionAsync", "b", "expand"]);
assert.equal(press(root, child, " "), true);
await new Promise(resolve => setImmediate(resolve));
assert.deepEqual(commands.pop(), ["InvokeKeyboardActionAsync", "a1", "check"]);
assert.equal(press(root, child, "Enter"), true);
await new Promise(resolve => setImmediate(resolve));
assert.deepEqual(commands.pop(), ["InvokeKeyboardActionAsync", "a1", "activate"]);
assert.equal(press(root, child, "Enter", { repeat: true }), true);
assert.equal(press(root, child, "Enter", { isComposing: true }), false);
const control = new Element(child);
assert.equal(press(root, control, " "), false);
assert.deepEqual(commands, []);

child.focus();
child.remove();
changed();
assert.equal(document.activeElement, beta);
beta.remove();
const replacement = node(root, "b", { "aria-selected": "true" });
changed();
assert.equal(document.activeElement, replacement);

const search = new Element(body);
search.focus();
replacement.remove();
changed();
assert.equal(document.activeElement, search);
alpha.focus();
const modal = new Element(body, { "aria-modal": "true" });
alpha.remove();
node(root, "c");
changed();
assert.equal(document.activeElement, body);
modal.remove();
window.clayTree.detachKeyboard(root);
assert.equal(root.events.size, 0);

console.log("ClayTree keyboard fake DOM: navigation, commands, checked, reload and focus safeguards passed");
