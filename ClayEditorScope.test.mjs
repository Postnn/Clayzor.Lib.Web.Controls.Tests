import assert from 'node:assert/strict';
import test from 'node:test';
import vm from 'node:vm';
import { webcrypto } from 'node:crypto';
import { readFile } from 'node:fs/promises';

const script = await readFile(new URL('../../src/Clayzor.Lib.Web.Controls/wwwroot/js/clayDraftStore.js', import.meta.url), 'utf8');

function createBrowser() {
    const keys = new Map();
    const drafts = new Map();
    const session = new Map();
    const listeners = new Map();
    let interceptWrite;
    const makeField = scope => ({
        type: 'text', value: '', disabled: false,
        dataset: { draftKey: scope + '.name', draftScope: scope },
        matches: () => true, closest: () => null,
    });
    const main = makeField('main');
    const child = makeField('child');
    const root = { contains: field => field === main || field === child };
    const document = {
        querySelectorAll: selector => selector === '#editor' ? [root] : [main, child],
        addEventListener: (name, listener) => listeners.set(name, listener),
        removeEventListener: name => listeners.delete(name),
    };
    const db = {
        transaction(name, mode) {
            const store = name === 'keys' ? keys : drafts;
            let aborted = false;
            let completed = false;
            const updates = [];
            const tx = {
                objectStore() {
                    return {
                        get(id) {
                            const request = { result: store.get(id) };
                            queueMicrotask(() => { if (!aborted) request.onsuccess?.(); });
                            return request;
                        },
                        put(entry) { updates.push(() => store.set(entry.id, entry)); return {}; },
                        delete(id) { updates.push(() => store.delete(id)); return {}; },
                    };
                },
                abort() {
                    if (completed || aborted) throw new DOMException('Inactive', 'InvalidStateError');
                    aborted = true;
                    setImmediate(() => tx.onabort?.());
                },
            };
            setImmediate(async () => {
                if (aborted) return;
                try {
                    if (name === 'drafts' && mode === 'readwrite' && interceptWrite) {
                        const intercept = interceptWrite;
                        interceptWrite = null;
                        await intercept();
                    }
                    if (aborted) return;
                    updates.forEach(apply => apply());
                    completed = true;
                    tx.oncomplete?.();
                } catch (error) {
                    tx.error = error;
                    tx.abort();
                }
            });
            return tx;
        },
    };

    const indexedDB = {
        open() {
            const request = { result: db };
            setImmediate(() => request.onsuccess());
            return request;
        },
    };
    const window = {};
    vm.runInNewContext(script, {
        indexedDB, crypto: webcrypto, navigator: {}, window, document, TextEncoder, TextDecoder, btoa, atob,
        sessionStorage: { getItem: key => session.get(key), setItem: (key, value) => session.set(key, value) },
    });
    return {
        store: window.clayDraftStore,
        open: () => window.clayDraftStore.register('territory', '4', 'employee', 1, '#editor'),
        writeHook: hook => { interceptWrite = hook; },
        input: (scope, value) => {
            const field = scope === 'main' ? main : child;
            field.value = value;
            listeners.get('input')({ target: field });
        },
    };
}

test('accepted reset clears only target raw and persists fresh baseline across registration', async () => {
    const browser = createBrowser();
    const { store } = browser;
    const { registrationId: id } = await browser.open();
    await store.saveTypedScope(id, 'main', 'local');
    await store.saveTypedScope(id, 'child', 'child local');
    browser.input('main', 'unfinished edit');
    browser.input('child', 'child raw');
    const state = await store.readEditorScope(id, 'main');
    assert.equal(state.hasRaw, true);
    assert.equal(await store.replaceEditorScope(id, 'main', state.revision, 'fresh'), true);

    assert.equal((await store.readEditorScope(id, 'main')).hasRaw, false);
    assert.equal((await store.readEditorScope(id, 'child')).hasRaw, true);
    await store.unregister(id);
    const reopened = await browser.open();
    assert.equal(reopened.typedScopes.main, 'fresh');
    assert.equal(reopened.typedScopes.child, 'child local');
});

test('new input during reset aborts the write and preserves the original and newer local data', async () => {
    const browser = createBrowser();
    const { store } = browser;
    const { registrationId: id } = await browser.open();
    await store.saveTypedScope(id, 'main', 'local');
    const state = await store.readEditorScope(id, 'main');
    const entered = Promise.withResolvers();
    const resume = Promise.withResolvers();
    browser.writeHook(() => {
        entered.resolve();
        return resume.promise;
    });
    const resetting = store.replaceEditorScope(id, 'main', state.revision, 'fresh');
    try {
        await entered.promise;
        const newer = store.saveTypedScope(id, 'child', 'newer');
        assert.equal(await resetting, false);
        await newer;
    } finally {
        resume.resolve();
    }
    await store.unregister(id);
    const reopened = await browser.open();
    assert.equal(reopened.typedScopes.main, 'local');
    assert.equal(reopened.typedScopes.child, 'newer');
});

test('storage failure preserves current raw and typed values; accepted close has a durable tombstone', async () => {
    const browser = createBrowser();
    const { store } = browser;
    const { registrationId: id } = await browser.open();
    await store.saveTypedScope(id, 'main', 'local');
    browser.input('main', 'unfinished edit');
    const state = await store.readEditorScope(id, 'main');
    browser.writeHook(() => { throw new Error('storage unavailable'); });
    await assert.rejects(store.replaceEditorScope(id, 'main', state.revision, null), /storage unavailable/);
    assert.equal((await store.readEditorScope(id, 'main')).hasRaw, true);
    await store.unregister(id);
    const recovered = await browser.open();
    assert.equal(recovered.typedScopes.main, 'local');

    const recoveredId = recovered.registrationId;
    const snapshot = await store.readEditorScope(recoveredId, 'main');
    assert.equal(await store.replaceEditorScope(recoveredId, 'main', snapshot.revision, null), true);
    await assert.rejects(store.saveTypedScope(recoveredId, 'main', 'late autosave'));
    await store.unregister(recoveredId);
    const closed = await browser.open();
    assert.equal(closed.typedScopes.main, undefined);
    assert.ok(closed.cancelledScopes.includes('main'));
});

test('unresolved operation survives scoped cancel and an old revision cannot reset newer input', async () => {
    const browser = createBrowser();
    const { store } = browser;
    const { registrationId: id } = await browser.open();
    await store.saveTypedScope(id, 'main', 'local');
    const old = await store.readEditorScope(id, 'main');
    await store.saveTypedScope(id, 'main', 'newer');
    assert.equal(await store.replaceEditorScope(id, 'main', old.revision, 'fresh'), false);
    await store.beginOperation(id, '{}');
    const pending = await store.readEditorScope(id, 'main');
    assert.equal(pending.hasOperation, true);
    assert.equal(await store.replaceEditorScope(id, 'main', pending.revision, null), false);
    await store.unregister(id);
    const reopened = await browser.open();
    assert.equal(reopened.typedScopes.main, 'newer');
    assert.ok(reopened.operation);
});
