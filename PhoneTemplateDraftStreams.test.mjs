import { Blob, Buffer } from "node:buffer";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import vm from "node:vm";

export async function verifyTemplateStreams() {
    const inventory = new URL("../../src/Kesco.App.Web.Inventory/wwwroot/js/phoneTemplateDraftText.js", import.meta.url);
    const controls = new URL("../../src/Clayzor.Lib.Web.Controls/wwwroot/js/clayDraftStore.js", import.meta.url);
    const large = "Ж".repeat(600000);
    const Capture = { createJSStreamReference: blob => ({ bytes: blob.size, blob }) };
    const js = await readFile(inventory, "utf8");
    const readTextStream = vm.runInNewContext(js.replace(/^export /gm, "") + "\nreadTextStream", { Blob, DotNet: Capture });
    const live = readTextStream({ querySelector: () => ({ value: large, disabled: false }) });
    assert.ok(live.bytes > 32768);
    assert.equal(await live.blob.text(), large);
    assert.equal(readTextStream({ querySelector: () => ({ disabled: true }) }), null);

    const source = await readFile(controls, "utf8");
    const begin = source.indexOf("    async function streamRestore(");
    const end = source.indexOf("    window.clayDraftStore = {", begin);
    let released = [];
    const context = {
        Blob, DotNet: Capture,
        window: { clayDraftStore: { unregister: id => released.push(id) } },
    };
    const pack = vm.runInNewContext(source.substring(begin, end) + "\nstreamRestore", context);
    const envelope = await pack({ registrationId: "fixture", typedScopes: { template: large } });
    assert.equal(envelope.registrationId, "fixture");
    assert.deepEqual(Object.keys(envelope), ["registrationId", "payload"]);
    assert.ok(envelope.payload.bytes > 32768);
    assert.equal(JSON.parse(await envelope.payload.blob.text()).typedScopes.template, large);
    context.DotNet = { createJSStreamReference() { throw new Error(large); } };
    await assert.rejects(() => pack({ registrationId: "fixture", typedScopes: { template: large } }),
        error => !error.message.includes(large));
    assert.deepEqual(released, ["fixture"]);
    return { liveBytes: live.bytes, restoreBytes: envelope.payload.bytes, cleanup: true };
}

export async function verifyTemplateWatcher() {
    const inventory = new URL("../../src/Kesco.App.Web.Inventory/wwwroot/js/phoneTemplateDraftText.js", import.meta.url);
    const control = { value: "Ж".repeat(600000), selectionStart: 19, selectionEnd: 22, selectionDirection: "backward" };
    let activeControl = control;
    const scoped = new Map();
    const timers = new Map();
    const closeCalls = [];
    const notifications = [];
    const wire = [];
    const captured = [];
    const indicator = { textContent: "" };
    const scope = {
        querySelector: selector => selector === "textarea" ? activeControl
            : selector === "[data-template-line]" ? indicator : null,
        addEventListener: (type, callback, capture) => {
            assert.equal(capture, true);
            scoped.set(type, callback);
        },
        removeEventListener: (type, callback, capture) => {
            assert.equal(capture, true);
            assert.equal(scoped.get(type), callback);
            scoped.delete(type);
        },
    };
    let timerId = 0;
    const sandbox = {
        Blob,
        DotNet: { createJSStreamReference: blob => ({ bytes: blob.size, blob }) },
        setTimeout(callback) { timers.set(++timerId, callback); return timerId; },
        clearTimeout(id) { timers.delete(id); },
        window: { clayModelEquipmentForm: { close() { closeCalls.push(true); } } },
    };
    const source = await readFile(inventory, "utf8");
    const hooks = vm.runInNewContext(source.replace(/^export /gm, "") +
        "\n({watchTemplateText, unwatchTemplateText, readTemplateSnapshot, currentTemplateRevision, closeTemplateIfCurrent, insertTemplateAttribute, findTemplateAttribute, freezeTemplateInput, releaseTemplateInput})", sandbox);

    function send(type, frameworkHandlers = false, isComposing = false, target = control) {
        let stopped = false;
        const event = { target, isComposing, stopPropagation: () => { stopped = true; },
            preventDefault: () => assert.fail("Native editing must not be prevented") };
        if (type === "input" || type === "change") {
            captured.push(target.value);              // shared store, document capture
            if (frameworkHandlers && type === "change") wire.push(target.value); // pinned Blazor capture
        }
        scoped.get(type)?.(event);
        if (frameworkHandlers && type === "input" && !stopped) wire.push(target.value); // Blazor bubble
    }

    async function flush() {
        const jobs = [...timers.values()];
        timers.clear();
        for (const job of jobs) await job();
    }

    send("input", true);
    send("change", true);
    assert.ok(wire.every(frame => Buffer.byteLength(frame) > 32768));
    assert.equal(wire.length, 2);
    wire.length = 0;

    hooks.watchTemplateText(scope, {
        async invokeMethodAsync(...args) {
            notifications.push(args);
        },
    }, "generation-1");
    send("input");     // paste; the browser has already updated value and selection
    send("change");    // blur in the opt-in branch, with no Blazor input/change handlers
    assert.equal(captured.at(-1), control.value);
    assert.deepEqual([control.selectionStart, control.selectionEnd, control.selectionDirection], [19, 22, "backward"]);
    assert.equal(wire.length, 0);
    await flush();
    assert.deepEqual(notifications, [["OnTemplateEditorChangedAsync", "generation-1", 2]]);
    const snapshot = hooks.readTemplateSnapshot(scope, "generation-1");
    assert.equal(snapshot.revision, 2);
    assert.equal(await snapshot.payload.blob.text(), control.value);
    assert.deepEqual(Object.keys(snapshot), ["revision", "payload"]);

    send("compositionstart");
    control.value += "文";
    send("input", false, true);
    assert.equal(hooks.currentTemplateRevision(scope, "generation-1"), null);
    assert.equal(hooks.closeTemplateIfCurrent(scope, "generation-1", 2), false);
    await flush();
    assert.equal(notifications.length, 1);
    send("compositionend");
    await flush();
    assert.equal(notifications.at(-1)[2], 3);
    assert.equal(hooks.closeTemplateIfCurrent(scope, "generation-1", 2), false);
    assert.equal(hooks.closeTemplateIfCurrent(scope, "generation-1", 3), true);
    assert.equal(closeCalls.length, 1);

    const unrelated = { value: "other input" };
    send("input", true, false, unrelated);
    assert.equal(wire.at(-1), unrelated.value);

    hooks.unwatchTemplateText(scope, "previous-generation");
    assert.equal(scoped.size, 9);
    send("input");
    hooks.unwatchTemplateText(scope, "generation-1");
    await flush();
    assert.equal(scoped.size, 0);
    assert.equal(hooks.currentTemplateRevision(scope, "generation-1"), null);
    assert.equal(notifications.length, 2);

    control.value = "Первая строка\nProxy.1 и proxy.1";
    control.maxLength = 400;
    control.scrollTop = 7;
    control.setSelectionRange = (start, end) => {
        control.selectionStart = start;
        control.selectionEnd = end;
    };
    control.focus = () => send("focus");
    control.setRangeText = (value, start, end, selection) => {
        assert.equal(selection, "end");
        control.value = control.value.slice(0, start) + value + control.value.slice(end);
        control.setSelectionRange(start + value.length, start + value.length);
    };
    control.dispatchEvent = event => {
        assert.equal(event.bubbles, true);
        send(event.type);
    };
    sandbox.Event = class { constructor(type, options) { this.type = type; this.bubbles = options.bubbles; } };
    hooks.watchTemplateText(scope, { async invokeMethodAsync() {} }, "tools");
    control.setSelectionRange(0, 0);
    assert.equal(hooks.insertTemplateAttribute(scope, "old", "Proxy"), false);
    assert.equal(hooks.insertTemplateAttribute(scope, "tools", "Proxy"), true);
    assert.equal(control.value, "?ProxyПервая строка\nProxy.1 и proxy.1");
    assert.equal(control.selectionStart, 6);
    assert.equal(control.scrollTop, 7);
    assert.equal(captured.at(-1), control.value);
    assert.equal(hooks.currentTemplateRevision(scope, "tools"), 1);
    assert.equal(indicator.textContent, "Строка 1");
    assert.equal(hooks.findTemplateAttribute(scope, "tools", "proxy.1"), true);
    assert.equal(control.value.slice(control.selectionStart, control.selectionEnd), "Proxy.1");
    send("select");
    assert.equal(indicator.textContent, "Строка 2");
    assert.equal(hooks.findTemplateAttribute(scope, "tools", "proxy.1"), true);
    assert.equal(control.value.slice(control.selectionStart, control.selectionEnd), "proxy.1");
    assert.equal(hooks.findTemplateAttribute(scope, "tools", "proxy.1"), true);
    assert.equal(control.value.slice(control.selectionStart, control.selectionEnd), "Proxy.1");
    assert.equal(hooks.currentTemplateRevision(scope, "tools"), 1);
    control.readOnly = true;
    assert.equal(hooks.insertTemplateAttribute(scope, "tools", "Proxy"), false);
    send("blur");
    assert.equal(indicator.textContent, "");
    control.readOnly = false;
    assert.equal(hooks.freezeTemplateInput(scope, "tools", 0), false);
    assert.equal(hooks.freezeTemplateInput(scope, "old", 1), false);
    send("compositionstart");
    assert.equal(hooks.freezeTemplateInput(scope, "tools", 1), false);
    send("compositionend");
    assert.equal(hooks.freezeTemplateInput(scope, "tools", 1), true);
    assert.equal(control.readOnly, true);
    assert.equal(hooks.insertTemplateAttribute(scope, "tools", "Proxy"), false);
    hooks.releaseTemplateInput(scope, "old");
    assert.equal(control.readOnly, true);
    const nextControl = { value: "fresh", readOnly: true };
    activeControl = nextControl;
    hooks.releaseTemplateInput(scope, "tools");
    assert.equal(control.readOnly, false);
    assert.equal(nextControl.readOnly, true);
    hooks.unwatchTemplateText(scope, "tools");
    return { ordinaryFramesOverLimit: 2, protectedNativeEvents: captured.length, notificationCount: notifications.length, closedAtCurrentRevisionOnly: true };
}
await verifyTemplateStreams();
await verifyTemplateWatcher();
