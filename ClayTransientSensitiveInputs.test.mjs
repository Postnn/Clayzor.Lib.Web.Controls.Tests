import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import vm from "node:vm";

const sourceUrl = new URL("../../src/Clayzor.Lib.Web.Controls/wwwroot/js/clayTransientSensitiveInputs.js", import.meta.url);
const source = await readFile(sourceUrl, "utf8");

function fixture() {
    let valueAccesses = 0;
    const listeners = new Map();
    const controls = [];
    const root = {
        isConnected: true,
        inert: false,
        querySelectorAll: () => controls,
        contains: control => controls.includes(control),
        addEventListener(name, handler, useCapture) {
            assert.equal(useCapture, true);
            assert.equal(listeners.has(name), false);
            listeners.set(name, handler);
        },
        removeEventListener(name, handler, useCapture) {
            assert.equal(useCapture, true);
            assert.equal(listeners.get(name), handler);
            listeners.delete(name);
        },
    };

    function control({ readOnly = false, disabled = false } = {}) {
        return {
            readOnly, disabled, matches: () => true,
            get value() { valueAccesses++; throw new Error("read protected value"); },
            set value(_) { valueAccesses++; throw new Error("wrote protected value"); },
        };
    }

    function emit(type, target, { isComposing = false, cancelable = true } = {}) {
        let prevented = false;
        listeners.get(type)?.({
            type, target, isComposing, cancelable,
            get data() { valueAccesses++; throw new Error("read input event data"); },
            preventDefault() { prevented = true; },
        });
        return prevented;
    }

    const api = vm.runInNewContext(source.replace(/^export function /gm, "function ") + `
        ({ watchTransientSensitiveInputs, readTransientSensitiveInputs, freezeTransientSensitiveInputs,
           transientSensitiveFreezeIsCurrent, releaseTransientSensitiveInputs, unwatchTransientSensitiveInputs });
    `);
    return { api, root, controls, control, listeners, emit, assertNoValueAccess: () => assert.equal(valueAccesses, 0) };
}

function state(f, generation = "editor-1") {
    const result = f.api.readTransientSensitiveInputs(f.root, generation);
    return result === null ? null : { ...result };
}

test("native edit before blur exposes only volatile metadata", () => {
    const f = fixture();
    const field = f.control();
    f.controls.push(field);
    assert.equal(f.api.watchTransientSensitiveInputs(f.root, "editor-1"), true);
    assert.deepEqual(state(f), { revision: 0, dirty: false, composing: false });
    assert.equal(f.emit("beforeinput", field), false);
    f.emit("input", field);
    assert.deepEqual(state(f), { revision: 1, dirty: true, composing: false });
    assert.equal(state(f, "other"), null);
    f.assertNoValueAccess();
});

test("IME blocks freezes and the earlier revision remains stale after composition ends", () => {
    const f = fixture();
    const field = f.control();
    f.controls.push(field);
    f.api.watchTransientSensitiveInputs(f.root, "editor-1");
    f.emit("compositionstart", field);
    f.emit("input", field, { isComposing: true });
    const composing = state(f);
    assert.equal(composing.composing, true);
    assert.equal(f.api.freezeTransientSensitiveInputs(f.root, "editor-1", composing.revision), null);
    assert.equal(field.readOnly, false);

    f.emit("compositionend", field);
    const ready = state(f);
    assert.equal(ready.composing, false);
    assert.equal(f.api.freezeTransientSensitiveInputs(f.root, "editor-1", composing.revision), null);
    const lease = f.api.freezeTransientSensitiveInputs(f.root, "editor-1", ready.revision);
    assert.equal(f.api.transientSensitiveFreezeIsCurrent(f.root, "editor-1", lease), true);
    assert.equal(f.emit("beforeinput", field), true);
    assert.equal(f.api.releaseTransientSensitiveInputs(f.root, "editor-1", lease), true);
    assert.equal(field.readOnly, false);
    assert.equal(state(f).dirty, true);
    f.assertNoValueAccess();
});

test("late input invalidates the held lease and only a current accepted reset clears dirty", () => {
    const f = fixture();
    const editable = f.control();
    const originalReadOnly = f.control({ readOnly: true });
    f.controls.push(editable, originalReadOnly);
    f.api.watchTransientSensitiveInputs(f.root, "editor-1");
    f.emit("input", editable);
    const first = f.api.freezeTransientSensitiveInputs(f.root, "editor-1", state(f).revision);
    assert.notEqual(first, null);
    assert.equal(f.root.inert, true);
    f.emit("input", editable);
    assert.equal(f.api.transientSensitiveFreezeIsCurrent(f.root, "editor-1", first), false);
    assert.equal(f.api.releaseTransientSensitiveInputs(f.root, "editor-1", first, true), true);
    assert.equal(state(f).dirty, true);
    assert.equal(f.root.inert, false);
    assert.equal(editable.readOnly, false);
    assert.equal(originalReadOnly.readOnly, true);

    const second = f.api.freezeTransientSensitiveInputs(f.root, "editor-1", state(f).revision);
    assert.notEqual(second, first);
    assert.equal(f.api.releaseTransientSensitiveInputs(f.root, "editor-1", first), false);
    assert.equal(f.api.releaseTransientSensitiveInputs(f.root, "stale-generation", second), false);
    assert.equal(f.root.inert, true);
    assert.equal(f.api.releaseTransientSensitiveInputs(f.root, "editor-1", second, true), true);
    assert.equal(state(f).dirty, false);
    assert.equal(f.root.inert, false);
    assert.equal(editable.readOnly, false);
    assert.equal(originalReadOnly.readOnly, true);
    f.assertNoValueAccess();
});

test("replacement, detachment and unwatch invalidate obsolete DOM leases", () => {
    const f = fixture();
    const initial = f.control();
    const replacement = f.control();
    f.controls.push(initial);
    f.api.watchTransientSensitiveInputs(f.root, "editor-1");
    const before = state(f).revision;
    f.controls.splice(0, 1, replacement);
    assert.equal(f.api.freezeTransientSensitiveInputs(f.root, "editor-1", before), null);
    const lease = f.api.freezeTransientSensitiveInputs(f.root, "editor-1", state(f).revision);
    assert.notEqual(lease, null);
    f.controls.splice(0, 1, initial);
    assert.equal(f.api.transientSensitiveFreezeIsCurrent(f.root, "editor-1", lease), false);
    f.root.isConnected = false;
    assert.equal(state(f), null);
    f.api.unwatchTransientSensitiveInputs(f.root, "editor-1");
    assert.equal(f.listeners.size, 0);
    assert.equal(replacement.readOnly, false);

    f.root.isConnected = true;
    assert.equal(f.api.watchTransientSensitiveInputs(f.root, "editor-2"), true);
    assert.equal(f.api.releaseTransientSensitiveInputs(f.root, "editor-1", lease, true), false);
    assert.deepEqual(state(f, "editor-2"), { revision: 0, dirty: false, composing: false });
    f.assertNoValueAccess();
});
