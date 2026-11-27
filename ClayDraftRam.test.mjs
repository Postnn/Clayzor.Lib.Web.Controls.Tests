import assert from 'node:assert/strict';
import test from 'node:test';
import vm from 'node:vm';
import { webcrypto } from 'node:crypto';
import { readFile } from 'node:fs/promises';

const script = await readFile(new URL('../../src/Clayzor.Lib.Web.Controls/wwwroot/js/clayDraftStore.js', import.meta.url), 'utf8');
const first = '00000000000000000000000000000001';
const second = '00000000000000000000000000000002';

function createDocument(drafts = new Map(), session = new Map(), origin = 'https://inventory.example') {
    const root = { contains: node => node === field };
    const field = {
        type: 'text', value: '', disabled: false,
        dataset: { draftKey: 'name' },
        matches: () => true, closest: () => null, getAttribute: () => null, dispatchEvent: () => {},
    };
    const listeners = new Map();
    const protectedWindow = { location: { origin } };
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
            baseURI: `${origin}/inventory/`,
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

test('loopback HTTP requests a key but remote HTTP is rejected before fetch', async () => {
    const local = createDocument(new Map(), new Map(), 'http://localhost:5020');
    await local.open();
    assert.equal(local.fetches[0].url, 'http://localhost:5020/inventory/api/drafts/key');

    const remote = createDocument(new Map(), new Map(), 'http://inventory.example');
    await assert.rejects(remote.open(), /HTTPS/);
    assert.equal(remote.fetches.length, 0);
});

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
