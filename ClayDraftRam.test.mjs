import assert from 'node:assert/strict';
import test from 'node:test';
import vm from 'node:vm';
import { webcrypto } from 'node:crypto';
import { readFile } from 'node:fs/promises';

const script = await readFile(new URL('../../src/Clayzor.Lib.Web.Controls/wwwroot/js/clayDraftStore.js', import.meta.url), 'utf8');
const first = '00000000000000000000000000000001';
const second = '00000000000000000000000000000002';

function createDocument(drafts = new Map(), session = new Map()) {
    const root = { contains: node => node === field };
    const field = {
        type: 'text', value: '', disabled: false,
        dataset: { draftKey: 'name' },
        matches: () => true, closest: () => null, getAttribute: () => null, dispatchEvent: () => {},
    };
    const listeners = new Map();
    const protectedWindow = { location: { origin: 'https://inventory.example' } };
    const fetches = [];
    const buffers = [];
    const reads = [];
    const stores = new Set();
    let fieldVisible = true;
    let refuseDelete = false;
    let writeHook;

    const db = {
        objectStoreNames: { contains: name => stores.has(name) },
        createObjectStore(name) { stores.add(name); },
        transaction(name, mode) {
            assert.equal(name, 'drafts', 'RAM mode never opens the legacy keys store');
            const changes = [];
            const tx = { objectStore: () => ({
                get(id) {
                    reads.push(id);
                    const request = { result: drafts.get(id) };
                    queueMicrotask(() => request.onsuccess?.());
                    return request;
                },
                put(record) { changes.push(() => drafts.set(record.id, record)); return {}; },
                delete(id) {
                    changes.push(() => {
                        if (refuseDelete) throw new Error('delete refused');
                        drafts.delete(id);
                    });
                    return {};
                },
            }) };
            setImmediate(async () => {
                try {
                    const hook = writeHook;
                    if (mode === 'readwrite' && hook) {
                        writeHook = undefined;
                        await hook();
                    }
                    for (const change of changes) change();
                    tx.oncomplete?.();
                } catch (error) {
                    tx.error = error;
                    tx.onabort?.();
                }
            });
            return tx;
        },
    };
    vm.runInNewContext(script, {
        indexedDB: { open(name) {
            assert.equal(name, 'clayzor-drafts-v2', 'RAM mode never opens the legacy database');
            const request = { result: db };
            queueMicrotask(() => { request.onupgradeneeded(); request.onsuccess(); });
            return request;
        } },
        crypto: webcrypto, URL, TextEncoder, TextDecoder, Event, navigator: {}, window: protectedWindow, btoa, atob, Blob,
        DotNet: { createJSStreamReference: value => value },
        document: {
            baseURI: 'https://inventory.example/inventory/',
            querySelectorAll: selector => selector === '#editor' ? [root] : fieldVisible ? [field] : [],
            addEventListener(name, listener) {
                if (!listeners.has(name)) listeners.set(name, new Set());
                listeners.get(name).add(listener);
            },
            removeEventListener: (name, listener) => listeners.get(name)?.delete(listener),
        },
        sessionStorage: {
            getItem: key => session.get(key),
            setItem: (key, value) => session.set(key, value),
            removeItem: key => session.delete(key),
        },
        async fetch(url, options) {
            fetches.push({ url: url.href, options });
            const material = webcrypto.getRandomValues(new Uint8Array(32));
            buffers.push(material);
            return {
                status: 200,
                headers: { get: name => name === 'X-Clay-Draft-Employee' ? '27' : 'application/octet-stream' },
                arrayBuffer: async () => material.buffer,
            };
        },
    });
    const store = protectedWindow.clayDraftStore;
    return {
        store, drafts, session, fetches, buffers, reads, stores,
        open: (id = first, employee = '27') => store.registerRam('territory', '4', employee, 1, '#editor', id),
        openEquipment: () => store.registerRam('equipment-form', '7', '27', 2, '#editor', first),
        stream: (id = first) => store.registerRamStream('territory', '4', '27', 1, '#editor', id),
        putHook: hook => { writeHook = hook; },
        showField: visible => { fieldVisible = visible; },
        refuseDelete: value => { refuseDelete = value; },
        input(value) {
            field.value = value;
            for (const listener of listeners.get('input') || []) listener({ target: field, isTrusted: true });
        },
        field,
    };
}

test('a changed revision keeps an editor open and preserves later input', async () => {
    const fixture = createDocument();
    const { registrationId } = await fixture.open();
    const before = await fixture.store.readEditorScope(registrationId, 'main');
    fixture.input('later input');

    assert.equal(await fixture.store.closeRam(registrationId, before.revision), false);
    assert.equal(fixture.field.value, 'later input');
    const latest = await fixture.store.readEditorScope(registrationId, 'main');
    assert.equal(latest.hasRaw, true);
    assert.equal(fixture.drafts.size, 1);
    assert.equal(await fixture.store.closeRam(registrationId, latest.revision), true);
    assert.equal(fixture.drafts.size, 0);
});

test('close state detects nested input without returning its values', async () => {
    const fixture = createDocument();
    const { registrationId } = await fixture.open();
    const before = await fixture.store.readEditorCloseState(registrationId);
    const nested = 'child:ups-test:2:root';
    await fixture.store.saveTypedScope(registrationId, nested, 'private typed value');
    fixture.field.dataset.draftScope = nested;
    fixture.input('private raw value');

    const main = await fixture.store.readEditorScope(registrationId, 'main');
    const current = await fixture.store.readEditorCloseState(registrationId);
    assert.equal(main.hasRaw, false);
    assert.deepEqual(JSON.parse(JSON.stringify(current)), {
        revision: main.revision, hasRaw: true, hasOperation: false, typedScopes: [nested],
    });
    assert.equal(await fixture.store.closeRam(registrationId, before.revision), false);
    assert.equal(fixture.field.value, 'private raw value');
    assert.equal(fixture.drafts.size, 1);
});

test('operation evidence used to close Equipment excludes private snapshots and requires the exact revision', async () => {
    const fixture = createDocument();
    const { registrationId } = await fixture.openEquipment();
    await fixture.store.beginOperationIntent(registrationId, JSON.stringify({
        version: 1, kind: 'equipment-save', acceptedScopes: ['main'], attemptId: first,
        data: JSON.stringify({
            Kind: 'equipment-save', EquipmentId: 7, Origin: 'main', Editor: null,
            Intent: { ExpectedSnapshot: 'private values', AfterSnapshot: 'other private values' },
        }),
    }));
    const close = await fixture.store.readEditorCloseState(registrationId);
    assert.equal(close.hasOperation, true);
    assert.deepEqual(JSON.parse(JSON.stringify(await fixture.store.readCloseOperationEvidence(registrationId, close.revision))), {
        attemptId: first, kind: 'equipment-save', data: '{}',
    });
    fixture.input('later input');
    await assert.rejects(fixture.store.readCloseOperationEvidence(registrationId, close.revision), /Изменилась операция/);
    assert.equal(await fixture.store.closeRam(registrationId, close.revision), false);
    assert.equal(fixture.drafts.size, 1);
});

test('concurrent attach and stream use one HTTPS request and reconnect restores typed and raw input', async () => {
    const fixture = createDocument();
    const [opened, streamed] = await Promise.all([fixture.open(), fixture.stream()]);
    assert.equal(opened.registrationId, streamed.registrationId);
    assert.equal(JSON.parse(await streamed.payload.text()).registrationId, opened.registrationId);
    assert.equal(fixture.fetches.length, 1);
    assert.deepEqual([...fixture.stores], ['drafts']);
    const { url, options } = fixture.fetches[0];
    assert.equal(url, 'https://inventory.example/inventory/api/drafts/key');
    assert.equal(options.method, 'POST');
    assert.equal(options.credentials, 'same-origin');
    assert.equal(options.cache, 'no-store');
    assert.equal(options.redirect, 'error');
    assert.equal(options.headers['X-Clay-Draft-Key'], '1');
    assert.ok(fixture.buffers[0].every(byte => byte === 0));

    fixture.input('local raw');
    await fixture.store.saveTypedScope(opened.registrationId, 'main', '{"name":"typed"}');
    assert.equal(fixture.drafts.size, 1);
    assert.doesNotMatch(JSON.stringify([...fixture.drafts.values()]), /typed|local raw/);
    assert.equal(await fixture.store.canReload(), false);

    await fixture.store.unregister(opened.registrationId);
    const attached = await fixture.open();
    assert.notEqual(attached.registrationId, opened.registrationId);
    assert.equal(fixture.fetches.length, 1);
    assert.equal(attached.typedScopes.main, '{"name":"typed"}');
    fixture.field.value = '';
    await fixture.store.applyRaw(attached.registrationId, 'main', false);
    assert.equal(fixture.field.value, 'local raw');
    await assert.rejects(fixture.store.saveTyped(opened.registrationId, 'late stale input'));
    await fixture.store.closeRam(attached.registrationId);
    await fixture.store.closeRam(attached.registrationId);

    assert.equal(fixture.drafts.size, 0);
    assert.equal(fixture.session.size, 0);
    await assert.rejects(fixture.open(), /уже закрыт/);
    await fixture.open(second);
    assert.equal(fixture.fetches.length, 2, 'the final owner releases its key');
});

test('close waits for a pending write, permits cleanup retry and preserves sibling owners', async () => {
    const fixture = createDocument();
    const [left, right] = await Promise.all([fixture.open(), fixture.open(second)]);
    assert.equal(fixture.fetches.length, 1);
    await fixture.store.saveTyped(right.registrationId, 'right');
    let enter;
    let release;
    const entered = new Promise(resolve => { enter = resolve; });
    const held = new Promise(resolve => { release = resolve; });
    fixture.putHook(() => { enter(); return held; });
    const inFlight = fixture.store.saveTyped(left.registrationId, 'left');
    await entered;
    fixture.refuseDelete(true);
    const closing = fixture.store.closeRam(left.registrationId);
    await assert.rejects(fixture.store.saveTyped(left.registrationId, 'late'));
    assert.equal(await fixture.store.canReload(), false);
    release();
    await inFlight;
    await assert.rejects(closing, /delete refused/);
    assert.equal(fixture.drafts.size, 2);
    fixture.refuseDelete(false);
    const leftId = [...fixture.drafts.keys()].find(key => key.includes(first));
    fixture.drafts.set(leftId + ':schema1-backup', { id: leftId + ':schema1-backup' });
    await fixture.store.closeRam(left.registrationId);
    assert.equal(fixture.drafts.size, 1);
    assert.equal(fixture.session.size, 1);
    assert.ok([...fixture.drafts.keys()][0].includes(second));
    await fixture.store.unregister(right.registrationId);
    const reopened = await fixture.open(second);
    assert.equal(reopened.typedState, 'right');
    assert.equal(fixture.fetches.length, 1);
});

test('a reloaded document never reads old ciphertext and changed employee revokes old handles', async () => {
    const old = createDocument();
    const previous = await old.open();
    await old.store.saveTyped(previous.registrationId, 'old');
    const newDocument = createDocument(old.drafts, old.session);
    const opened = await newDocument.open();
    assert.equal(opened.hasDraft, false);
    assert.notEqual(newDocument.reads[0], old.reads[0]);
    await newDocument.store.saveTyped(opened.registrationId, 'new');
    await newDocument.store.closeRam(opened.registrationId);
    assert.equal(old.drafts.size, 1);
    assert.equal(old.session.size, 1);

    await assert.rejects(old.open(second, '99'), /Сотрудник/);
    await assert.rejects(old.open(), /Сотрудник/);
    await assert.rejects(old.store.saveTyped(previous.registrationId, 'wrong user'));
    assert.equal(old.fetches.length, 1);
    assert.equal(await old.store.canReload(), false);
});

test('close prevents a pending local begin from authorizing a business command', async () => {
    const fixture = createDocument();
    const { registrationId } = await fixture.open();
    fixture.input('existing input');
    await fixture.store.saveTyped(registrationId, 'baseline');
    let entered;
    let release;
    const started = new Promise(resolve => { entered = resolve; });
    const held = new Promise(resolve => { release = resolve; });
    fixture.putHook(() => { entered(); return held; });
    const begin = fixture.store.beginOperationIntent(registrationId, JSON.stringify({
        version: 1, kind: 'test-operation', acceptedScopes: ['main'], data: '{}',
    }));
    await started;
    const closing = fixture.store.closeRam(registrationId);
    release();
    await assert.rejects(begin, /not active/);
    await closing;
    assert.equal(fixture.drafts.size, 0);
});

test('reopening a committed empty record after a failed delete does not report a draft', async () => {
    const fixture = createDocument();
    const { registrationId } = await fixture.open();
    const sibling = await fixture.open(second);
    await fixture.store.saveTypedScope(registrationId, 'main', 'submitted');
    await fixture.store.saveTypedScope(sibling.registrationId, 'main', 'sibling');
    const { revision } = JSON.parse(await fixture.store.beginOperationIntent(registrationId, JSON.stringify({
        version: 1, kind: 'test-save', acceptedScopes: ['main'], data: '{}',
    })));

    await fixture.store.recordConfirmedOperation(registrationId, revision);
    fixture.refuseDelete(true);
    await assert.rejects(fixture.store.commitOperation(registrationId, revision), /delete refused/);
    assert.equal(fixture.drafts.size, 2);
    await fixture.store.unregister(registrationId);
    const reopened = await fixture.open();
    assert.equal(reopened.hasDraft, false);
    assert.equal(reopened.operation, null);
    assert.equal(fixture.drafts.size, 2);

    fixture.refuseDelete(false);
    await fixture.store.unregister(reopened.registrationId);
    assert.equal((await fixture.open()).hasDraft, false);
    assert.equal(fixture.drafts.size, 1);
    assert.equal((await fixture.open(second)).typedScopes.main, 'sibling');
});

for (const status of ['running', 'unknown']) {
    test(`cleanup rejects ${status} and preserves the exact attempt and input until confirmation`, async () => {
        const fixture = createDocument();
        const { registrationId } = await fixture.open();
        fixture.input('unsubmitted browser text');
        await fixture.store.saveTypedScope(registrationId, 'main', 'submitted typed text');
        const intent = JSON.stringify({
            version: 1, kind: 'test-save', acceptedScopes: ['main'], data: '{}',
        });
        const attempt = JSON.parse(await fixture.store.beginOperationIntent(registrationId, intent));
        if (status === 'unknown')
            await fixture.store.unknownOperation(registrationId, attempt.revision);

        const before = await fixture.store.getHealth(registrationId);
        await assert.rejects(fixture.store.commitOperation(registrationId, attempt.revision), /подтвердите исход/);
        assert.equal((await fixture.store.getHealth(registrationId)).revision, before.revision);
        await fixture.store.unregister(registrationId);
        const reopened = await fixture.open();
        const restored = JSON.parse(reopened.operation);
        assert.equal(restored.attemptId, attempt.attemptId);
        assert.equal(restored.revision, attempt.revision);
        assert.equal(restored.status, status);
        assert.equal(reopened.typedScopes.main, 'submitted typed text');
        fixture.field.value = '';
        await fixture.store.applyRaw(reopened.registrationId, 'main', false);
        assert.equal(fixture.field.value, 'unsubmitted browser text');
        await assert.rejects(fixture.store.beginOperationIntent(reopened.registrationId, intent), /не сверена/);

        await fixture.store.recordConfirmedOperation(reopened.registrationId, restored.revision);
        fixture.input('later browser text');
        await fixture.store.saveTypedScope(reopened.registrationId, 'main', 'later typed text');
        await fixture.store.commitOperation(reopened.registrationId, restored.revision);
        await fixture.store.commitOperation(reopened.registrationId, restored.revision);
        await fixture.store.unregister(reopened.registrationId);
        const after = await fixture.open();
        assert.equal(after.operation, null);
        assert.equal(after.typedScopes.main, 'later typed text');
        fixture.field.value = '';
        await fixture.store.applyRaw(after.registrationId, 'main', false);
        assert.equal(fixture.field.value, 'later browser text');
    });
}

test('healthy storage rejects accepting a scope whose raw input could not be restored', async () => {
    const fixture = createDocument();
    const { registrationId } = await fixture.open();
    fixture.input('unsent raw input');
    await fixture.store.saveTypedScope(registrationId, 'main', 'initial');
    await fixture.store.unregister(registrationId);
    fixture.showField(false);
    fixture.field.value = '';
    const reopened = await fixture.open();
    const handle = reopened.registrationId;
    await fixture.store.applyRaw(handle, 'main', false);
    assert.equal((await fixture.store.getRestoreReadiness(handle)).complete, false);
    assert.equal((await fixture.store.getHealth(handle)).healthy, true);

    const intent = JSON.stringify({ version: 1, kind: 'test-save', acceptedScopes: ['main'], data: '{}' });
    await assert.rejects(fixture.store.beginOperationIntent(handle, intent), /восстановите ввод/);
    assert.equal((await fixture.store.getHealth(handle)).hasOperation, false);
    fixture.showField(true);
    await fixture.store.applyRaw(handle, 'main', false);
    assert.equal(fixture.field.value, 'unsent raw input');
    assert.equal((await fixture.store.getRestoreReadiness(handle)).complete, true);
    assert.equal(JSON.parse(await fixture.store.beginOperationIntent(handle, intent)).status, 'running');
});

test('an unreadable owned RAM record is replaced without losing live input or another editor', async () => {
    const fixture = createDocument();
    const opened = await fixture.open();
    const id = fixture.reads.at(-1);
    await fixture.store.saveTypedScope(opened.registrationId, 'main', '{"name":"live"}');
    await fixture.store.unregister(opened.registrationId);
    const other = await fixture.open(second);
    const otherId = fixture.reads.at(-1);
    await fixture.store.saveTypedScope(other.registrationId, 'main', '{"name":"other"}');
    const otherRecord = fixture.drafts.get(otherId);

    fixture.drafts.get(id).data = 'AAAA';
    const attached = await fixture.open();
    assert.equal(attached.typedScopes.main, '{"name":"live"}');
    assert.notEqual(fixture.drafts.get(id).data, 'AAAA');
    assert.equal(fixture.drafts.get(otherId), otherRecord);
    await fixture.store.closeRam(attached.registrationId);
    await fixture.store.closeRam(other.registrationId);
});