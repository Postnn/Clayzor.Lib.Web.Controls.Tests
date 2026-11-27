import assert from 'node:assert/strict';
import test from 'node:test';
import vm from 'node:vm';
import { webcrypto } from 'node:crypto';
import { readFile } from 'node:fs/promises';

const script = await readFile(new URL('../../src/Clayzor.Lib.Web.Controls/wwwroot/js/clayDraftStore.js', import.meta.url), 'utf8');

function createBrowser(formKey = 'territory') {
    const keys = new Map();
    const drafts = new Map();
    const session = new Map();
    const listeners = new Map();
    const heldLocks = new Set();
    let interceptWrite;
    let rejectDelete = false;
    let inspectSnapshot;
    const crypto = {
        randomUUID: () => webcrypto.randomUUID(),
        getRandomValues: bytes => webcrypto.getRandomValues(bytes),
        subtle: new Proxy(webcrypto.subtle, {
            get(target, name) {
                if (name !== 'decrypt') return target[name].bind(target);
                return async (...args) => {
                    const bytes = await target.decrypt(...args);
                    if (!inspectSnapshot) return bytes;
                    const inspect = inspectSnapshot;
                    inspectSnapshot = null;
                    const state = JSON.parse(new TextDecoder().decode(bytes));
                    inspect(state);
                    return new TextEncoder().encode(JSON.stringify(state)).buffer;
                };
            },
        }),
    };
    const makeField = scope => ({
        type: 'text', value: '', disabled: false,
        dataset: { draftKey: scope + '.name', draftScope: scope },
        matches: () => true, closest: () => null, getAttribute: () => null, dispatchEvent: () => {},
    });
    const main = makeField('main');
    const secondary = makeField('main');
    secondary.dataset.draftKey = 'main.secondary';
    const child = makeField('child');
    const fields = { main, secondary, child };
    const root = { contains: field => Object.values(fields).includes(field) };
    const document = {
        querySelectorAll: selector => selector === '#editor' ? [root] : Object.values(fields),
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
                        getAll({ lower, upper }) {
                            const request = { result: [...store.entries()].filter(([id]) => id >= lower && id <= upper).map(([, value]) => value) };
                            return request;
                        },
                        put(entry) { updates.push(() => store.set(entry.id, entry)); return {}; },
                        delete(id) {
                            updates.push(() => {
                                if (rejectDelete) throw new Error('delete refused');
                                store.delete(id);
                            });
                            return {};
                        },
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
    const window = { addEventListener: (name, listener) => listeners.set(name, listener) };
    vm.runInNewContext(script, {
        indexedDB, crypto, window, document, TextEncoder, TextDecoder, Event, btoa, atob,
        IDBKeyRange: { bound: (lower, upper) => ({ lower, upper }) },
        navigator: { locks: { async request(name, _, callback) {
            if (heldLocks.has(name)) return callback(null);
            heldLocks.add(name);
            try { return await callback({ name }); }
            finally { heldLocks.delete(name); }
        } } },
        sessionStorage: { getItem: key => session.get(key), setItem: (key, value) => session.set(key, value) },
    });
    return {
        store: window.clayDraftStore,
        beforeUnload: () => {
            let prevented = false;
            listeners.get('beforeunload')({ preventDefault: () => { prevented = true; } });
            return prevented;
        },
        open: (entity = '4', user = 'employee') => window.clayDraftStore.register(formKey, entity, user, 1, '#editor'),
        forgetPointer: () => session.clear(),
        rejectDeletes: value => { rejectDelete = value; },
        mountValue: (name, value) => { fields[name].value = value; },
        setDisabled: (name, disabled) => { fields[name].disabled = disabled; },
        setType: (name, type) => { fields[name].type = type; },
        setDraftKey: (name, key) => { fields[name].dataset.draftKey = key; },
        inspectNextSnapshot: callback => { inspectSnapshot = callback; },
        onDispatch: (name, handler) => { fields[name].dispatchEvent = handler; },
        displayedValue: name => fields[name].value,
        setClaySelect: (name, restoreDraftText) => {
            const field = fields[name];
            field.id = name;
            field.getAttribute = key => key === 'role' ? 'combobox' : null;
            field.closest = selector => selector === '.clay-select' ? root : null;
            window.claySelect = { restoreDraftText };
        },
        writeHook: hook => { interceptWrite = hook; },
        input: (name, value, isTrusted = false) => {
            const field = fields[name];
            field.value = value;
            listeners.get('input')({ target: field, isTrusted });
        },
    };
}

test('raw capture excludes password inputs and known sensitive keys shown as text', async () => {
    const browser = createBrowser('equipment-form');
    const { store } = browser;
    browser.setType('secondary', 'password');
    browser.setDraftKey('child', 'sim.puk');
    const { registrationId: id } = await browser.open();
    browser.input('secondary', 'synthetic password');
    browser.input('child', 'synthetic code');
    browser.input('main', 'ordinary text');
    const attempt = JSON.parse(await store.beginOperationIntent(id, JSON.stringify({
        version: 1, kind: 'fixture', data: '{}', acceptedScopes: ['main', 'child'],
    })));
    assert.deepEqual(Object.keys(attempt.scopes[0].rawRevisions), ['data:main.name:0']);
    assert.deepEqual(Object.keys(attempt.scopes[1].rawRevisions), []);
});

test('restoring an owned historical snapshot scrubs secrets but keeps Unknown and ordinary raw', async () => {
    const browser = createBrowser('equipment-form');
    const { store } = browser;
    const { registrationId: id } = await browser.open();
    browser.input('main', 'ordinary typed input');
    browser.input('child', 'ordinary child input');
    await store.saveTypedScope(id, 'child', 'retained sibling scope');
    const attempt = JSON.parse(await store.beginOperationIntent(id, JSON.stringify({
        version: 1, kind: 'fixture', data: '{}', acceptedScopes: ['main'],
    })));
    await store.unknownOperation(id, attempt.revision);
    await store.unregister(id);
    browser.setType('secondary', 'password');
    browser.mountValue('secondary', 'fresh authorized input');
    browser.inspectNextSnapshot(state => {
        state.raw['data:account.password:0'] = { kind: 'value', value: 'synthetic password' };
        state.raw['data:sim.puk:1'] = { kind: 'value', scope: 'child', value: 'synthetic code' };
        state.raw['data:main.secondary:0'] = { kind: 'value', scope: 'main', value: 'synthetic value' };
    });
    const restored = await browser.open();
    const marker = JSON.parse(restored.operation);
    assert.equal(marker.status, 'unknown');
    assert.equal(marker.revision, attempt.revision);
    assert.equal(restored.typedScopes.child, 'retained sibling scope');
    const result = await store.applyRaw(restored.registrationId);
    assert.deepEqual([...result.applied], ['data:main.name:0', 'data:child.name:0']);
    assert.deepEqual([...result.pending], []);
    assert.equal(browser.displayedValue('main'), 'ordinary typed input');
    assert.equal(browser.displayedValue('child'), 'ordinary child input');
    assert.equal(browser.displayedValue('secondary'), 'fresh authorized input');

    await store.unregister(restored.registrationId);
    browser.inspectNextSnapshot(state => assert.deepEqual(Object.keys(state.raw), ['data:main.name:0', 'data:child.name:0']));
    await browser.open();
});

test('reconnect blocks a full reload after IndexedDB has persisted unsaved fields', async () => {
    const browser = createBrowser();
    const { registrationId } = await browser.open();
    assert.equal(await browser.store.canReload(), true);

    await browser.store.saveTyped(registrationId, '{"name":"updated"}');
    assert.equal((await browser.store.getHealth(registrationId)).healthy, true);
    assert.equal(await browser.store.canReload(), false);

    await browser.store.discard(registrationId);
    await browser.open();
    assert.equal(await browser.store.canReload(), true);
});

test('a server-side unregister with unsaved raw input keeps reload protection', async () => {
    const browser = createBrowser();
    const { registrationId } = await browser.open();
    assert.equal(browser.beforeUnload(), false);
    browser.input('main', 'not saved to SQL');
    assert.equal(browser.beforeUnload(), true);
    await browser.store.unregister(registrationId);

    const clean = await browser.open('5');
    assert.equal((await browser.store.getHealth(clean.registrationId)).healthy, true);
    assert.equal(await browser.store.canReload(), false);
    assert.equal(browser.beforeUnload(), true);

    const recovered = await browser.open();
    await browser.store.discard(recovered.registrationId);
    assert.equal(await browser.store.canReload(), true);
    assert.equal(browser.beforeUnload(), false);
});

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
test('durable confirmed receipt survives failed cleanup and prevents downgrade', async () => {
    const browser = createBrowser();
    const { store } = browser;
    const { registrationId: id } = await browser.open();
    await store.saveTypedScope(id, 'main', 'submitted');
    await store.saveTypedScope(id, 'child', 'unrelated');
    const attempt = JSON.parse(await store.beginOperationIntent(id, JSON.stringify({
        version: 1, attemptId: 'attempt-1', kind: 'test', data: 'submitted', acceptedScopes: ['main'],
    })));
    await store.recordConfirmedOperation(id, attempt.revision);
    await assert.rejects(store.unknownOperation(id, attempt.revision));
    await assert.rejects(store.rejectOperation(id, attempt.revision));
    await store.saveTypedScope(id, 'main', 'newer after dispatch');

    browser.writeHook(() => { throw new Error('cleanup unavailable'); });
    await assert.rejects(store.commitOperation(id, attempt.revision), /cleanup unavailable/);
    await assert.rejects(store.unregister(id));

    const restored = await browser.open();
    const receipt = JSON.parse(restored.operation);
    assert.equal(receipt.attemptId, attempt.attemptId);
    assert.equal(receipt.revision, attempt.revision);
    assert.equal(receipt.status, 'confirmed');
    await store.recordConfirmedOperation(restored.registrationId, receipt.revision);
    await store.commitOperation(restored.registrationId, receipt.revision);
    await store.unregister(restored.registrationId);
    const final = await browser.open();
    assert.equal(final.operation, null);
    assert.equal(final.typedScopes.main, 'newer after dispatch');
    assert.equal(final.typedScopes.child, 'unrelated');
});

test('finds the same user and entity after losing the pointer without taking an active tab', async () => {
    const browser = createBrowser();
    const { store } = browser;
    const first = await browser.open();
    await store.saveTypedScope(first.registrationId, 'main', 'existing local');
    const prior = await store.beginOperation(first.registrationId, '{}');
    await store.recordConfirmedOperation(first.registrationId, prior);
    browser.forgetPointer();

    const empty = await browser.open();
    const candidates = await store.listRecoveryCandidates(empty.registrationId);
    assert.equal(candidates.length, 1);
    assert.equal(candidates[0].hasOperation, true);
    assert.equal(await store.recoverListedDraft(empty.registrationId, candidates[0].draftId), null);

    const otherEntity = await browser.open('5');
    const otherUser = await browser.open('4', 'different employee');
    assert.equal((await store.listRecoveryCandidates(otherEntity.registrationId)).length, 0);
    assert.equal((await store.listRecoveryCandidates(otherUser.registrationId)).length, 0);

    await store.unregister(first.registrationId);
    const restored = await store.recoverListedDraft(empty.registrationId, candidates[0].draftId);
    assert.equal(restored.registrationId, empty.registrationId);
    assert.equal(restored.typedScopes.main, 'existing local');
    assert.equal(JSON.parse(restored.operation).status, 'confirmed');
    assert.equal((await store.recoverListedDraft(empty.registrationId, candidates[0].draftId)).typedScopes.main,
        'existing local');
});

test('recovery cannot merge or overwrite input already made in the new registration', async () => {
    const browser = createBrowser();
    const { store } = browser;
    const first = await browser.open();
    await store.saveTypedScope(first.registrationId, 'main', 'stored');
    await store.unregister(first.registrationId);
    browser.forgetPointer();

    const empty = await browser.open();
    const [candidate] = await store.listRecoveryCandidates(empty.registrationId);
    await store.saveTypedScope(empty.registrationId, 'main', 'new independent draft');
    assert.equal(await store.recoverListedDraft(empty.registrationId, candidate.draftId), null);
    await store.unregister(empty.registrationId);
    const current = await browser.open();
    assert.equal(current.typedScopes.main, 'new independent draft');
    assert.equal((await store.listRecoveryCandidates(current.registrationId)).length, 1);
});

test('healthy storage rejects an accepted unrestored scope and preserves it during an unrelated commit', async () => {
    const browser = createBrowser();
    const { store } = browser;
    const first = await browser.open();
    browser.input('main', 'unrestored last character!');
    await store.unregister(first.registrationId);
    browser.mountValue('main', 'rendered default');

    const { registrationId: id } = await browser.open();
    assert.equal((await store.getHealth(id)).healthy, true);
    assert.ok((await store.getRestoreReadiness(id)).pendingScopes.includes('main'));
    const createIntent = scope => JSON.stringify({
        version: 1, kind: 'fixture', data: '{}', acceptedScopes: [scope],
    });

    await assert.rejects(store.beginOperationIntent(id, createIntent('main')), /восстановите/);
    assert.equal((await store.getHealth(id)).hasOperation, false);
    const child = JSON.parse(await store.beginOperationIntent(id, createIntent('child')));
    await store.recordConfirmedOperation(id, child.revision);
    await store.commitOperation(id, child.revision);
    assert.ok((await store.getRestoreReadiness(id)).pendingScopes.includes('main'));
    const restored = await store.applyRaw(id, 'main');
    assert.equal(restored.pending.length, 0);
    assert.equal(browser.displayedValue('main'), 'unrestored last character!');
});

test('failed deletion after accepted cleanup leaves a clean classification on re-registration', async () => {
    const browser = createBrowser();
    const { store } = browser;
    const first = await browser.open();
    await store.saveTypedScope(first.registrationId, 'main', 'submitted');
    const attempt = JSON.parse(await store.beginOperationIntent(first.registrationId, JSON.stringify({
        version: 1, kind: 'fixture', data: '{}', acceptedScopes: ['main', 'child'],
    })));
    await store.recordConfirmedOperation(first.registrationId, attempt.revision);
    browser.rejectDeletes(true);
    await assert.rejects(store.commitOperation(first.registrationId, attempt.revision), /delete refused/);
    await store.unregister(first.registrationId);

    const reopened = await browser.open();
    assert.equal(reopened.hasDraft, false);
    assert.equal(reopened.operation, null);
    assert.equal(Object.keys(reopened.typedScopes).length, 0);
    browser.rejectDeletes(false);
    await store.unregister(reopened.registrationId);
    const clean = await browser.open();
    assert.equal(clean.hasDraft, false);
    assert.equal((await store.getHealth(clean.registrationId)).healthy, true);
});

test('nested reset replaces the parent snapshot and only the selected scope in one revision', async () => {
    const browser = createBrowser();
    const { store } = browser;
    const { registrationId: id } = await browser.open();
    browser.input('main', 'retained parent raw');
    browser.input('child', 'discarded child raw');
    await store.saveTyped(id, '{"Model":"local","ActiveEditorState":"outdated child"}');
    await store.saveTypedScope(id, 'sibling', 'retained sibling');
    const original = await store.readEditorScope(id, 'child');
    const replacement = '{"Model":"local","ActiveEditorState":"fresh child"}';

    browser.input('child', 'newer child raw');
    await store.getHealth(id);
    assert.equal(await store.replaceNestedEditorScope(id, 'child', original.revision, replacement, false), false);
    const current = await store.readEditorScope(id, 'child');
    assert.equal(await store.replaceNestedEditorScope(id, 'child', current.revision, replacement, false), true);
    assert.equal((await store.readEditorScope(id, 'child')).hasRaw, false);
    assert.equal((await store.readEditorScope(id, 'main')).hasRaw, true);

    await store.unregister(id);
    const restored = await browser.open();
    assert.equal(restored.typedState, replacement);
    assert.equal(restored.typedScopes.sibling, 'retained sibling');
    assert.equal((await store.readEditorScope(restored.registrationId, 'child')).hasRaw, false);
    const closing = await store.readEditorScope(restored.registrationId, 'child');
    const parentWithoutEditor = '{"Model":"local","ActiveEditorState":null}';
    assert.equal(await store.replaceNestedEditorScope(restored.registrationId, 'child', closing.revision, parentWithoutEditor, true), true);
    await assert.rejects(store.saveTypedScope(restored.registrationId, 'child', 'late input'), /Отменённая область/);

    await store.unregister(restored.registrationId);
    const closed = await browser.open();
    assert.equal(closed.typedState, parentWithoutEditor);
    assert.equal(closed.typedScopes.sibling, 'retained sibling');
    assert.ok(closed.cancelledScopes.includes('child'));
});

test('failed field rebase preserves original typed and raw values in memory and storage', async () => {
    const browser = createBrowser();
    const { store } = browser;
    const { registrationId: id } = await browser.open();
    await store.saveTypedScope(id, 'main', 'old baseline');
    browser.input('main', 'unfinished input');
    browser.input('child', 'independent input');
    const review = await store.readRebaseState(id, 'main');

    browser.writeHook(() => { throw new Error('rebase storage unavailable'); });
    assert.equal(await store.rebaseScope(id, 'main', review.revision, 'new baseline', review.rawKeys), false);
    assert.equal((await store.readEditorScope(id, 'main')).hasRaw, true);
    assert.equal((await store.readEditorScope(id, 'child')).hasRaw, true);
    await store.unregister(id);

    const restored = await browser.open();
    assert.equal(restored.typedScopes.main, 'old baseline');
    await store.applyRaw(restored.registrationId, 'main');
    assert.equal(browser.displayedValue('main'), 'unfinished input');
});

test('a later edit aborts field rebase and preserves both the old baseline and the new raw input', async () => {
    const browser = createBrowser();
    const { store } = browser;
    const { registrationId: id } = await browser.open();
    await store.saveTypedScope(id, 'main', 'old baseline');
    browser.input('main', 'local');
    const review = await store.readRebaseState(id, 'main');
    const entered = Promise.withResolvers();
    const resume = Promise.withResolvers();
    browser.writeHook(() => {
        entered.resolve();
        return resume.promise;
    });

    const rebasing = store.rebaseScope(id, 'main', review.revision, 'new baseline', review.rawKeys);
    try {
        await entered.promise;
        browser.input('main', 'later edit');
        assert.equal(await rebasing, false);
    } finally {
        resume.resolve();
    }
    await store.unregister(id);
    const restored = await browser.open();
    assert.equal(restored.typedScopes.main, 'old baseline');
    await store.applyRaw(restored.registrationId, 'main');
    assert.equal(browser.displayedValue('main'), 'later edit');

    const accepted = await store.readRebaseState(restored.registrationId, 'main');
    assert.equal(await store.rebaseScope(restored.registrationId, 'main',
        accepted.revision, 'accepted baseline', accepted.rawKeys), true);
    assert.equal((await store.readEditorScope(restored.registrationId, 'main')).hasRaw, false);
});

test('pending retry restores only unavailable fields in the requested scope', async () => {
    const browser = createBrowser();
    const { store } = browser;
    const first = await browser.open();
    browser.input('main', 'previous text');
    browser.input('secondary', 'locked text');
    browser.input('child', 'separate text');
    await store.unregister(first.registrationId);

    browser.mountValue('main', '');
    browser.mountValue('secondary', '');
    browser.mountValue('child', '');
    browser.setDisabled('secondary', true);
    const { registrationId: id } = await browser.open();
    const initial = await store.applyPendingRaw(id, 'main');
    assert.equal(initial.applied.join(), 'data:main.name:0');
    assert.equal(initial.pending.join(), 'data:main.secondary:0');

    browser.mountValue('main', 'newer rendered text');
    browser.setDisabled('secondary', false);
    await store.reportRestoreScopeFailure(id, 'main');
    await store.reportRestoreScopeFailure(id, 'child');
    const retry = await store.applyPendingRaw(id, 'main');
    assert.equal(retry.applied.join(), 'data:main.secondary:0');
    assert.equal(browser.displayedValue('main'), 'newer rendered text');
    assert.equal(browser.displayedValue('secondary'), 'locked text');
    assert.equal(browser.displayedValue('child'), '');
    const readiness = await store.getRestoreReadiness(id);
    assert.equal(readiness.pendingScopes.join(), 'child');
    assert.equal(readiness.failedScopes.join(), 'child');
    await store.unregister(id);
});

test('an explicit clear supersedes pending raw without replaying the older text', async () => {
    const browser = createBrowser();
    const { store } = browser;
    const first = await browser.open();
    browser.input('main', 'protected text');
    await store.unregister(first.registrationId);
    browser.setDisabled('main', true);
    const { registrationId: id } = await browser.open();
    assert.equal((await store.applyPendingRaw(id, 'main')).pending.length, 1);
    browser.setDisabled('main', false);
    browser.input('main', '', true);

    const retry = await store.applyPendingRaw(id, 'main');
    assert.equal(retry.applied.length, 0);
    assert.equal(browser.displayedValue('main'), '');
    assert.equal((await store.getRestoreReadiness(id)).complete, true);
    await store.unregister(id);

    browser.mountValue('main', 'database text');
    const reopened = await browser.open();
    await store.applyPendingRaw(reopened.registrationId, 'main');
    assert.equal(browser.displayedValue('main'), '');
    await store.unregister(reopened.registrationId);
});

test('ClaySelect raw remains pending until the matching editor accepts it', async () => {
    const browser = createBrowser();
    const { store } = browser;
    const first = await browser.open();
    browser.input('main', 'unconfirmed lookup');
    await store.unregister(first.registrationId);
    browser.mountValue('main', '');
    const { registrationId: id } = await browser.open();
    browser.setClaySelect('main', async () => false);
    assert.equal((await store.applyPendingRaw(id, 'main')).pending.length, 1);
    assert.equal((await store.getRestoreReadiness(id)).pendingScopes.join(), 'main');
    browser.setClaySelect('main', async (name, value) => {
        assert.equal(name, 'main');
        assert.equal(value, 'unconfirmed lookup');
        browser.mountValue(name, value);
        return true;
    });
    assert.equal((await store.applyPendingRaw(id, 'main')).applied.length, 1);
    assert.equal(browser.displayedValue('main'), 'unconfirmed lookup');
    assert.equal((await store.getRestoreReadiness(id)).complete, true);
    await store.unregister(id);
});

test('pending silent retry recovers textarea text without dispatching it as input', async () => {
    const browser = createBrowser();
    const { store } = browser;
    const first = await browser.open();
    browser.input('main', 'template body');
    await store.unregister(first.registrationId);
    browser.mountValue('main', '');
    let events = 0;
    browser.onDispatch('main', () => { events++; });

    const reopened = await browser.open();
    const restored = await store.applyPendingRaw(reopened.registrationId, 'main', false);
    assert.equal(restored.applied.length, 1);
    assert.equal(browser.displayedValue('main'), 'template body');
    assert.equal(events, 0);
    await store.unregister(reopened.registrationId);
});

test('exclusive confirmation blocks a competing tab and an orphaned operation in the same scope', async () => {
    const browser = createBrowser();
    const { store } = browser;
    const first = await browser.open();
    browser.forgetPointer();
    const second = await browser.open();
    const start = registrationId => store.beginOperationIntent(registrationId, JSON.stringify({
        version: 1, kind: 'equipment.create', data: '{}', acceptedScopes: ['main'], blockOtherDraftOperations: true,
    }));
    const attempts = await Promise.allSettled([start(first.registrationId), start(second.registrationId)]);
    assert.equal(attempts.filter(attempt => attempt.status === 'fulfilled').length, 1);
    const winner = attempts[0].status === 'fulfilled' ? first : second;
    const other = winner === first ? second : first;
    const accepted = JSON.parse(attempts.find(attempt => attempt.status === 'fulfilled').value);
    await store.unknownOperation(winner.registrationId, accepted.revision);
    await store.unregister(winner.registrationId);
    await assert.rejects(start(other.registrationId), /В другом черновике/);
    assert.equal((await store.getHealth(other.registrationId)).hasOperation, false);

    const separate = await browser.open('different-source');
    const independent = JSON.parse(await start(separate.registrationId));
    assert.equal(independent.status, 'running');
    await store.unregister(other.registrationId);
    await store.unregister(separate.registrationId);
});
