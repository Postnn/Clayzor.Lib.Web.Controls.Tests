import assert from 'node:assert/strict';
import test from 'node:test';
import vm from 'node:vm';
import { webcrypto } from 'node:crypto';
import { readFile } from 'node:fs/promises';

const script = await readFile(new URL('../../src/Clayzor.Lib.Web.Controls/wwwroot/js/clayDraftStore.js', import.meta.url), 'utf8');
const employee = '27';
const secret = 'never expose the stored raw secret';
const guid = id => '00000000-0000-0000-0000-' + String(id).padStart(12, '0');
const attempt = id => guid(id + 100).replaceAll('-', '');

async function fixture({ exists = true, withKey = true, locks = true, keyOwner = employee } = {}) {
    const key = await webcrypto.subtle.generateKey({ name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
    const keyId = Buffer.from(await webcrypto.subtle.digest('SHA-256', new TextEncoder().encode(keyOwner))).toString('base64');
    const records = new Map();
    const session = new Map();
    const calls = [];
    const held = new Set();
    let closed = false;
    let creationAborted = false;

    const db = {
        close() { closed = true; },
        transaction(name, mode) {
            assert(mode === 'readonly' || name === 'drafts' && mode === 'readwrite');
            calls.push(name);
            const deleting = [];
            let aborted = false;
            const tx = { objectStore: () => ({
                get(id) {
                    const request = { result: name === 'keys' ? (id === keyId && withKey ? { key } : undefined) : records.get(id) };
                    queueMicrotask(() => { if (!aborted) request.onsuccess?.(); });
                    return request;
                },
                getAllKeys({ lower, upper }) {
                    return { result: [...records.keys()].filter(id => id >= lower && id <= upper) };
                },
                delete(id) { deleting.push(id); },
            }), abort() { aborted = true; } };
            setImmediate(() => {
                if (aborted) {
                    tx.onabort?.();
                    return;
                }
                for (const id of deleting) records.delete(id);
                tx.oncomplete?.();
            });
            return tx;
        },
    };
    const window = {};
    const webLocks = { async request(name, _options, callback) {
        if (held.has(name)) return callback(null);
        held.add(name);
        try { return await callback({ name }); }
        finally { held.delete(name); }
    } };
    vm.runInNewContext(script, {
        crypto: webcrypto, window, TextEncoder, TextDecoder, btoa, atob,
        IDBKeyRange: { bound: (lower, upper) => ({ lower, upper }) },
        navigator: locks ? { locks: webLocks } : {},
        sessionStorage: {
            getItem: key => session.get(key),
            removeItem: key => session.delete(key),
        },
        indexedDB: { open(name, version) {
            assert.equal(name, 'clayzor-drafts-v1');
            assert.equal(version, undefined);
            const request = { result: db, transaction: { abort() {
                creationAborted = true;
                queueMicrotask(() => request.onerror());
            } } };
            queueMicrotask(() => exists ? request.onsuccess() : request.onupgradeneeded({ oldVersion: 0 }));
            return request;
        } },
    });
    return {
        records, calls, held, session,
        get closed() { return closed; },
        get creationAborted() { return creationAborted; },
        read: (form = 'maintenance', entity = 'list', principal = employee) =>
            window.clayDraftStore.readLegacyOperationEvidence(form, entity, principal),
        begin: (leaseId, form = 'maintenance', entity = 'list', principal = employee, retire = false) =>
            window.clayDraftStore.beginLegacyOperationEvidenceTransfer(form, entity, principal, leaseId, retire),
        retire: leaseId => window.clayDraftStore.retireLegacyOperationEvidenceTransfer(leaseId),
        finish: leaseId => window.clayDraftStore.finishLegacyOperationEvidenceTransfer(leaseId),
        oldBegin: (form, entity, principal = employee) =>
            webLocks.request('clay-draft-operation:' + JSON.stringify([principal, form, entity]), { ifAvailable: true },
                lock => lock !== null),
        async put(id, status, options = {}) {
            const { userKey = employee, ownerKey = userKey, form = 'maintenance', entity = 'list',
                operation, backup = false } = options;
            const draftId = guid(id);
            const snapshot = {
                format: 1, userKey: ownerKey, formKey: form, entityKey: entity, draftId,
                typedScopes: { main: { data: secret } }, raw: { secret: { value: secret } },
                operation: status === null ? null : {
                    version: 1, attemptId: attempt(id), kind: 'maintenance.complete', status,
                    data: String(id), ...operation,
                },
            };
            const iv = webcrypto.getRandomValues(new Uint8Array(12));
            const bytes = await webcrypto.subtle.encrypt({ name: 'AES-GCM', iv }, key, new TextEncoder().encode(JSON.stringify(snapshot)));
            const storageId = [userKey, form, entity, draftId].join('|') + (backup ? ':schema1-backup' : '');
            records.set(storageId, { id: storageId, iv: Buffer.from(iv).toString('base64'), data: Buffer.from(bytes).toString('base64') });
        },
    };
}

test('legacy evidence is a narrow read-only projection and never returns stored editor data', async () => {
    const f = await fixture();
    await f.put(1, 'running');
    await f.put(2, 'unknown');
    await f.put(3, 'confirmed');
    await f.put(3, 'confirmed', { backup: true });
    await f.put(4, null);
    await f.put(5, 'unknown', { userKey: '28', operation: { data: secret } });

    const result = JSON.parse(JSON.stringify(await f.read()));
    assert.deepEqual(result, [1, 2, 3].map(id => ({ attemptId: attempt(id), kind: 'maintenance.complete', data: String(id) })));
    assert.doesNotMatch(JSON.stringify(result), /confirmed|running|unknown|secret|typed|raw/);
    assert.equal(f.records.size, 6);
    assert.equal(f.held.size, 0);
    assert.equal(f.closed, true);
});

test('active legacy tab or legacy begin lock blocks migration without touching its record', async () => {
    const f = await fixture();
    await f.put(1, 'unknown');
    const tab = 'clay-draft:maintenance:list:' + guid(1);
    f.held.add(tab);
    await assert.rejects(f.read(), /другой вкладке/);
    assert.equal(f.records.size, 1);
    assert.equal(f.held.size, 1);
    f.held.delete(tab);
    f.held.add('clay-draft-operation:["27","maintenance","list"]');
    await assert.rejects(f.read(), /Другая вкладка/);
    assert.equal(f.records.size, 1);
});

test('retirement deletes only exact owned snapshots and their current pointer after acknowledged transfer', async () => {
    const f = await fixture();
    await f.put(1, 'unknown');
    await f.put(1, 'unknown', { backup: true });
    await f.put(2, null);
    await f.put(3, 'unknown', { userKey: '28' });
    await f.put(4, null, { form: 'duty', entity: '20240229' });
    f.session.set('clay-draft:27:maintenance:list', guid(1));
    f.session.set('clay-draft:28:maintenance:list', guid(3));
    const lease = guid(101);
    assert.equal((await f.begin(lease, 'maintenance', 'list', employee, true)).length, 1);
    assert.equal(f.records.size, 5);
    assert.equal(await f.oldBegin('maintenance', 'list'), false);

    await f.retire(lease);
    await f.finish(lease);

    assert.deepEqual([...f.records.keys()], [
        `28|maintenance|list|${guid(3)}`, `27|duty|20240229|${guid(4)}`,
    ]);
    assert.equal(f.session.has('clay-draft:27:maintenance:list'), false);
    assert.equal(f.session.get('clay-draft:28:maintenance:list'), guid(3));
    assert.equal(f.held.size, 0);
});

test('abandoned import and changed legacy ciphertext keep all snapshots and pointers', async () => {
    const f = await fixture();
    await f.put(1, 'unknown');
    await f.put(2, null);
    const pointer = 'clay-draft:27:maintenance:list';
    f.session.set(pointer, guid(1));

    await f.begin(guid(101), 'maintenance', 'list', employee, true);
    await f.finish(guid(101));
    await assert.rejects(f.retire(guid(101)), /не подтверждён/);
    assert.equal(f.records.size, 2);

    await f.begin(guid(102), 'maintenance', 'list', employee, true);
    await f.put(2, null);
    await assert.rejects(f.retire(guid(102)), /изменился/);
    await f.finish(guid(102));

    assert.equal(f.records.size, 2);
    assert.equal(f.session.get(pointer), guid(1));
    assert.equal(f.held.size, 0);
});

test('aggregate creation transfer only retires an alias when its exact draft has transferred evidence', async () => {
    const f = await fixture();
    const form = 'territory-form';
    const oldScope = 'new:19';
    await f.put(1, null, { form, entity: oldScope });
    await f.put(2, 'unknown', { form, entity: oldScope, operation: {
        kind: 'territory.create',
        data: JSON.stringify({
            Original: { Record: { Id: 0, ParentId: 19 } },
            Current: { Record: { Id: 0, ParentId: 19 } },
        }),
    } });
    await f.put(2, null, { form, entity: oldScope, backup: true });
    await f.put(3, null, { form, entity: 'new' });
    const oldPointer = 'clay-draft:27:territory-form:' + oldScope;
    f.session.set(oldPointer, guid(1));
    const lease = guid(101);

    const evidence = await f.begin(lease, form, 'new', employee, true);
    assert.equal(evidence.length, 1);
    assert.equal(evidence[0].attemptId, attempt(2));
    await f.retire(lease);
    await f.finish(lease);

    assert.deepEqual([...f.records.keys()], [`27|territory-form|${oldScope}|${guid(1)}`]);
    assert.equal(f.session.get(oldPointer), guid(1));
});

test('PingHost retires only its exact typed journal and refuses unexpected operation evidence', async () => {
    const f = await fixture();
    const form = 'ping-host';
    await f.put(1, null, { form, entity: 'localhost' });
    await f.put(2, null, { form, entity: 'localhost.example' });
    const lease = guid(101);
    assert.equal((await f.begin(lease, form, 'localhost', employee, true)).length, 0);
    await f.retire(lease);
    await f.finish(lease);
    assert.deepEqual([...f.records.keys()], [`27|ping-host|localhost.example|${guid(2)}`]);

    await f.put(3, 'unknown', { form, entity: 'localhost', operation: { kind: 'unexpected.action', data: '{}' } });
    await assert.rejects(f.begin(guid(102), form, 'localhost', employee, true), /безопасно прочитать/);
    await assert.rejects(f.begin(guid(103), form, 'localhost|other', employee, true), /Неверный контекст/);
    assert.equal(f.records.size, 2);
    assert.equal(f.held.size, 0);
});

test('opening an absent database aborts creation and reading never creates missing keys', async () => {
    const absent = await fixture({ exists: false });
    assert.equal((await absent.read()).length, 0);
    assert.equal(absent.creationAborted, true);
    assert.deepEqual(absent.calls, []);

    const noKey = await fixture({ withKey: false });
    await noKey.put(1, 'running', { userKey: '28' });
    assert.equal((await noKey.read()).length, 0);
    assert.equal(noKey.calls.includes('keys'), false);
    await noKey.put(2, 'running');
    await assert.rejects(noKey.read(), /Ключ/);
    assert.equal(noKey.records.size, 2);
});

test('unsupported operation status, kind or data fails closed without exposing the payload', async () => {
    for (const [status, operation] of [
        ['rejected', {}], ['unknown', { kind: 'other' }], ['confirmed', { data: secret }],
        ['unknown', { data: '01' }], ['running', { data: '2147483648' }], ['running', { data: 1 }],
    ]) {
        const f = await fixture();
        await f.put(1, status, { operation });
        await assert.rejects(f.read(), /безопасно прочитать/);
        assert.equal(f.records.size, 1);
        assert.equal(f.held.size, 0);
    }
    const noLock = await fixture({ locks: false });
    await noLock.put(1, 'confirmed');
    await assert.rejects(noLock.read(), /проверить открытые вкладки/);
});

test('Duty, Beeline and PPA return only scoped identifiers from actual editor snapshots', async () => {
    const f = await fixture();
    const commercial = '000AB-001';
    const pool = '27\u001fСвязь:тип';
    const dutyData = JSON.stringify({ Original: null, Draft: { Date: '2024-02-29', PlannedId: 123, Notes: secret } });
    await f.put(1, 'running', { form: 'duty', entity: '20240229', operation: { kind: 'duty.save', data: dutyData } });
    await f.put(2, 'unknown', { form: 'duty', entity: 'new', operation: { kind: 'duty.save', data: dutyData } });
    await f.put(3, 'confirmed', { form: 'beeline-parameters', entity: commercial, operation: {
        kind: 'beeline.update', data: JSON.stringify({
            Original: null, Current: { TimeZone: secret, EmailManager: secret }, Version: { Key: commercial },
        }),
    } });
    for (const [i, action] of ['insert', 'update', 'delete'].entries()) {
        await f.put(4 + i, 'unknown', { form: 'phone-pool-attribute', entity: pool, operation: {
            kind: 'phone-pool-attribute.' + action,
            data: JSON.stringify({ Current: { Value: secret }, Version: i === 0 ? null : { Key: pool } }),
        } });
    }
    await f.put(7, 'unknown', { form: 'duty', entity: '20240229', userKey: '28',
        operation: { kind: 'duty.save', data: dutyData } });

    const actual = await Promise.all([
        f.read('duty', '20240229'), f.read('duty', 'new'),
        f.read('beeline-parameters', commercial), f.read('phone-pool-attribute', pool),
    ]);
    assert.deepEqual(JSON.parse(JSON.stringify(actual)), [
        [{ attemptId: attempt(1), kind: 'duty.save', data: '2024-02-29' }],
        [{ attemptId: attempt(2), kind: 'duty.save', data: '2024-02-29' }],
        [{ attemptId: attempt(3), kind: 'beeline.update', data: commercial }],
        ['insert', 'update', 'delete'].map((action, i) => ({ attemptId: attempt(4 + i), kind: 'phone-pool-attribute.' + action, data: pool })),
    ]);
    assert.doesNotMatch(JSON.stringify(actual), /secret|TimeZone|EmailManager|Value|PlannedId/);
    assert.equal(f.records.size, 7);
    assert.equal(f.held.size, 0);
});

test('unsupported snapshots, wrong owners and active tabs fail closed for the new scopes', async () => {
    for (const [form, entity, kind, data] of [
        ['duty', '20240229', 'duty.save', '{"Draft":{"Date":"2024-03-01"}}'],
        ['duty', 'new', 'duty.save', '{"Draft":{"Date":"2025-02-29"}}'],
        ['duty', 'new', 'duty.save', '{"Draft":{"Date":"2024-02-29T00:00:00"}}'],
        ['beeline-parameters', '000AB-001', 'beeline.update', '{"Current":{"TimeZone":"T","EmailManager":"E"},"Version":{"Key":"other"}}'],
        ['phone-pool-attribute', '27:тип', 'phone-pool-attribute.delete', '{"Current":{"Unexpected":"secret"}}'],
    ]) {
        const f = await fixture();
        await f.put(1, 'unknown', { form, entity, operation: { kind, data } });
        await assert.rejects(f.read(form, entity), /безопасно прочитать/);
        assert.equal(f.records.size, 1);
        assert.equal(f.held.size, 0);
    }

    const f = await fixture();
    const form = 'phone-pool-attribute';
    const entity = '27:тип';
    const operation = { kind: form + '.update', data: '{"Current":{"Value":"secret"}}' };
    await f.put(1, 'unknown', { form, entity, ownerKey: '28', operation });
    await assert.rejects(f.read(form, entity), /подтвердить владельца/);
    const tab = 'clay-draft:' + form + ':' + entity + ':' + guid(1);
    f.held.add(tab);
    await assert.rejects(f.read(form, entity), /другой вкладке/);
    assert.equal(f.records.size, 1);
    assert.deepEqual([...f.held], [tab]);
    await assert.rejects(f.read(form, '027:тип'), /Неверный контекст/);
    await assert.rejects(f.read(form, '27:😀'), /Неверный контекст/);
    await assert.rejects(f.read('duty', '20250229'), /Неверный контекст/);
    await assert.rejects(f.read('duty', 'new', '027'), /Неверный контекст/);
});

test('delimiter-joined storage prefixes do not include a neighboring entity or its active lock', async () => {
    const f = await fixture();
    const form = 'beeline-parameters';
    const entity = '000AB-001';
    const neighbor = entity + '|suffix';
    const operation = { kind: 'beeline.update', data: '{"Current":{"TimeZone":"T","EmailManager":"E"}}' };
    await f.put(1, 'running', { form, entity, operation });
    await f.put(2, 'running', { form, entity: neighbor, operation });
    f.held.add('clay-draft:' + form + ':' + neighbor + ':' + guid(2));

    const result = JSON.parse(JSON.stringify(await f.read(form, entity)));
    assert.deepEqual(result, [{ attemptId: attempt(1), kind: 'beeline.update', data: entity }]);
    assert.equal(f.records.size, 2);
    assert.equal(f.held.size, 1);
});

test('Net PoE, save, switch and exchange preserve only canonical targets', async () => {
    const f = await fixture();
    const form = 'inventory.net';
    const entity = '41';
    await f.put(1, 'confirmed', { form, entity, operation: { kind: 'net.poe', data: entity } });
    const targets = [[null, null], [75, null], [null, 88], [-1, -1], [75, -1]];
    for (const [index, [SwitchToId, ExchangeWithId]] of targets.entries()) {
        await f.put(index + 2, 'unknown', { form, entity, operation: { kind: 'net.save', data: JSON.stringify({
            PortId: 41, ExpectedChangedAtUtc: '2026-11-26T04:30:00Z', SwitchToId, ExchangeWithId,
            Fingerprint: secret, Notes: secret,
        }) } });
    }

    const actual = JSON.parse(JSON.stringify(await f.read(form, entity)));
    assert.deepEqual(actual, [
        { attemptId: attempt(1), kind: 'net.poe', data: '41' },
        ...targets.map(([SwitchToId, ExchangeWithId], index) => ({
            attemptId: attempt(index + 2), kind: 'net.save', data: JSON.stringify({
                PortId: 41, SwitchToId: SwitchToId === -1 ? null : SwitchToId,
                ExchangeWithId: ExchangeWithId === -1 ? null : ExchangeWithId,
            }),
        })),
    ]);
    assert.doesNotMatch(JSON.stringify(actual), /secret|Notes|Fingerprint|ChangedAt/);
    assert.equal(f.records.size, 6);
});

test('Tel projects only saved phone identity and aggregates its canonical own creation scopes', async () => {
    const f = await fixture();
    const form = 'inventory.tel';
    const make = (PhoneId, Deleting) => ({
        kind: 'tel.write', data: JSON.stringify({ PhoneId, Deleting, ChangedAtUtc: '0001-01-01T00:00:00', Fingerprint: secret }),
    });
    await f.put(1, 'confirmed', { form, entity: '42', operation: make(42, true) });
    await f.put(2, 'running', { form, entity: 'new:0', operation: make(0, false) });
    await f.put(3, 'unknown', { form, entity: 'new:7', operation: make(0, false) });
    await f.put(4, 'unknown', { form, entity: 'new:8', userKey: '28', operation: make(0, false) });
    await f.put(5, 'unknown', { form, entity: 'new', operation: make(0, false) });

    const item = (id, PhoneId, Deleting) => ({
        attemptId: attempt(id), kind: 'tel.write', data: JSON.stringify({ PhoneId, Deleting }),
    });
    const read = async key => JSON.parse(JSON.stringify(await f.read(form, key)));
    assert.deepEqual(await read('42'), [item(1, 42, true)]);
    assert.deepEqual(await read('new:7'), [item(3, 0, false)]);
    assert.deepEqual(await read('new'), [item(5, 0, false), item(2, 0, false), item(3, 0, false)]);
    f.held.add('clay-draft:' + form + ':new:7:' + guid(3));
    await assert.rejects(read('new'), /другой вкладке/);
    assert.equal(f.held.size, 1);
    assert.equal(f.records.size, 5);
});

test('Net and Tel reject mismatched keys and unsupported operations without inventing targets', async () => {
    for (const [form, entity, kind, data] of [
        ['inventory.net', '41', 'net.poe', '42'],
        ['inventory.net', '41', 'net.poe', '041'],
        ['inventory.net', '41', 'net.save', '{"PortId":41,"SwitchToId":42,"ExchangeWithId":43}'],
        ['inventory.net', '41', 'net.save', '{"PortId":41,"SwitchToId":41,"ExchangeWithId":null}'],
        ['inventory.net', '41', 'net.save', '{"PortId":41,"SwitchToId":null,"ExchangeWithId":41}'],
        ['inventory.net', '41', 'net.save', '{"PortId":42,"SwitchToId":null,"ExchangeWithId":null}'],
        ['inventory.net', '41', 'net.save', '{"PortId":41,"SwitchToId":null}'],
        ['inventory.net', '41', 'net.save', '{"PortId":41,"SwitchToId":"42","ExchangeWithId":null}'],
        ['inventory.net', '41', 'net.save', '{"PortId":41,"SwitchToId":0,"ExchangeWithId":null}'],
        ['inventory.net', '41', 'net.save', '{"PortId":41,"SwitchToId":-2,"ExchangeWithId":null}'],
        ['inventory.net', '41', 'net.save', '{"PortId":41,"SwitchToId":2147483648,"ExchangeWithId":null}'],
        ['inventory.tel', '42', 'tel.write', '{"PhoneId":41,"Deleting":false,"ChangedAtUtc":"time","Fingerprint":"hash"}'],
        ['inventory.tel', 'new:0', 'tel.write', '{"PhoneId":0,"Deleting":true,"ChangedAtUtc":"time","Fingerprint":"hash"}'],
        ['inventory.tel', '42', 'tel.write', '{"PhoneId":42,"Deleting":"false","ChangedAtUtc":"time","Fingerprint":"hash"}'],
        ['inventory.tel', '42', 'tel.write', '{"PhoneId":42,"Deleting":false}'],
    ]) {
        const f = await fixture();
        await f.put(1, 'unknown', { form, entity, operation: { kind, data } });
        await assert.rejects(f.read(form, entity), /безопасно прочитать/);
        assert.equal(f.records.size, 1);
        assert.equal(f.held.size, 0);
    }

    const f = await fixture();
    await assert.rejects(f.read('inventory.net', '041'), /Неверный контекст/);
    await assert.rejects(f.read('inventory.tel', 'new:01'), /Неверный контекст/);
    await f.put(1, 'unknown', { form: 'inventory.tel', entity: 'new:07', operation: { kind: 'tel.write' } });
    await assert.rejects(f.read('inventory.tel', 'new'), /подтвердить владельца|определить владельца/);
    assert.equal(f.records.size, 1);
});

test('legacy transfer retains all discovered Tel alias and draft locks during backend acknowledgement', async () => {
    const f = await fixture();
    const form = 'inventory.tel';
    const operation = { kind: 'tel.write', data: '{"PhoneId":0,"Deleting":false,"ChangedAtUtc":"time","Fingerprint":"hash"}' };
    await f.put(1, 'running', { form, entity: 'new:0', operation });
    await f.put(2, 'unknown', { form, entity: 'new:7', operation });
    const lease = guid(7);
    let acknowledge;
    const backend = new Promise(resolve => { acknowledge = resolve; });
    const evidence = await f.begin(lease, form, 'new');
    assert.equal(evidence.length, 2);
    const transfer = backend.finally(() => f.finish(lease));
    try {
        assert.equal(f.closed, true);
        assert.equal(await f.oldBegin(form, 'new'), false);
        assert.equal(await f.oldBegin(form, 'new:0'), false);
        assert.equal(await f.oldBegin(form, 'new:7'), false);
        assert.equal(await f.oldBegin(form, 'new:8'), true);
        assert.equal(f.held.has('clay-draft:' + form + ':new:0:' + guid(1)), true);
        assert.equal(f.held.has('clay-draft:' + form + ':new:7:' + guid(2)), true);
        await assert.rejects(f.begin(lease.replaceAll('-', ''), form, 'new'), /уже использован/);
        await assert.rejects(f.read(form, 'new:7'), /Другая вкладка/);
    } finally {
        acknowledge();
        await transfer;
    }
    await f.finish(lease);
    assert.equal(await f.oldBegin(form, 'new:7'), true);
    assert.equal(f.held.size, 0);
    assert.equal(f.records.size, 2);
});

test('missing or malformed evidence and an early finish never leak legacy locks', async () => {
    const f = await fixture();
    await f.put(1, 'unknown', { operation: { kind: 'unsupported' } });
    await assert.rejects(f.begin(guid(9)), /безопасно прочитать/);
    assert.equal(f.held.size, 0);
    await f.finish(guid(9));
    await assert.rejects(f.begin(guid(9)), /уже использован/);
    await assert.rejects(f.begin('invalid'), /Неверный идентификатор/);

    const aborting = await fixture();
    await aborting.put(1, 'running');
    const pending = assert.rejects(aborting.begin(guid(10)), /уже завершена/);
    await aborting.finish(guid(10));
    await pending;
    assert.equal(aborting.held.size, 0);

    const outOfOrder = await fixture();
    await outOfOrder.finish(guid(11));
    await assert.rejects(outOfOrder.begin(guid(11)), /уже использован/);
    assert.equal(outOfOrder.held.size, 0);

    const empty = await fixture({ exists: false });
    assert.equal((await empty.begin(guid(12))).length, 0);
    assert.equal(await empty.oldBegin('maintenance', 'list'), false);
    await empty.finish(guid(12));
    assert.equal(await empty.oldBegin('maintenance', 'list'), true);
    assert.equal(empty.creationAborted, true);

    const noLocks = await fixture({ exists: false, locks: false });
    await assert.rejects(noLocks.begin(guid(13)), /проверить открытые вкладки/);
    assert.equal(noLocks.creationAborted, false);
});

test('ModelEquipment and PhoneTemplate expose only the stable attempt and canonical model ID', async () => {
    const f = await fixture();
    const model = 'inventory.model-equipment';
    const template = 'inventory.phone-template';
    await f.put(1, 'unknown', {
        form: model, entity: '17',
        operation: { kind: 'equipment-model.save', data: JSON.stringify({
            Id: 17, ExpectedChangedAt: '2026-11-26T10:00:00', Fingerprint: 'a'.repeat(64), Name: secret,
        }) },
    });
    await f.put(2, 'confirmed', {
        form: template, entity: '17',
        operation: { kind: 'equipment-phone-template.save',
            data: JSON.stringify({ Id: 17, ExpectedChangedAt: null, Template: secret, Password: secret }) },
    });

    const models = JSON.parse(JSON.stringify(await f.read(model, '17')));
    const templates = JSON.parse(JSON.stringify(await f.read(template, '17')));
    assert.deepEqual(models, [{ attemptId: attempt(1), kind: 'equipment-model.save', data: '17' }]);
    assert.deepEqual(templates, [{ attemptId: attempt(2), kind: 'equipment-phone-template.save', data: '17' }]);
    assert.equal(f.records.size, 2);
});

test('ModelEquipment create transfer keeps locks on the actual signed initial type variants', async () => {
    const f = await fixture();
    const form = 'inventory.model-equipment';
    const operation = { kind: 'equipment-model.save',
        data: JSON.stringify({ Id: 0, ExpectedChangedAt: null, Fingerprint: 'F'.repeat(64), PartNumber: secret }) };
    for (const [id, entity] of [[1, 'new:0'], [2, 'new:70'], [3, 'new:-1']])
        await f.put(id, 'running', { form, entity, operation });
    const lease = guid(401);

    const result = JSON.parse(JSON.stringify(await f.begin(lease, form, 'new')));
    assert.deepEqual(result, [1, 2, 3].map(id => ({ attemptId: attempt(id), kind: operation.kind, data: '0' })));
    try {
        assert.equal(await f.oldBegin(form, 'new:0'), false);
        assert.equal(await f.oldBegin(form, 'new:70'), false);
        assert.equal(await f.oldBegin(form, 'new:-1'), false);
        assert.equal(await f.oldBegin(form, 'new:42'), true);
        assert.ok(f.held.has('clay-draft:' + form + ':new:-1:' + guid(3)));
    } finally {
        await f.finish(lease);
    }
    assert.equal(f.held.size, 0);
    assert.equal(f.records.size, 3);
});

test('EquipmentIns create and copy share the legacy import without exporting equipment values', async () => {
    const f = await fixture();
    const form = 'equipment-ins';
    const kind = 'equipment.create';
    const entries = [[1, '0:0'], [2, '17:0'], [3, '-1:8']];
    for (const [id, entity] of entries)
        await f.put(id, 'unknown', { form, entity,
            operation: { kind, data: JSON.stringify({ Source: entity, SerialNumber: secret, MacAddress: secret }) },
        });
    const lease = guid(419);
    const result = JSON.parse(JSON.stringify(await f.begin(lease, form, 'new')));
    assert.deepEqual(result, entries.map(([id, data]) => ({ attemptId: attempt(id), kind, data })));
    try {
        for (const [, entity] of entries)
            assert.equal(await f.oldBegin(form, entity), false);
    } finally {
        await f.finish(lease);
    }
    assert.equal(f.held.size, 0);
    assert.equal(f.records.size, entries.length);
});

test('LocationFact and territory transfer only own numeric operation metadata', async () => {
    const f = await fixture();
    const fact = 'location-fact';
    const tree = 'territory-tree-move';
    const from = { Id: 4, ParentId: null, Left: 0, Right: 3 };
    const to = { Id: 8, ParentId: 1, Left: 4, Right: 5 };
    await f.put(1, 'confirmed', { form: fact, entity: 'workplace', operation: {
        kind: 'confirm-location',
        data: JSON.stringify({ Expected: { Site: { Id: 9, Name: secret }, Equipment: { EquipmentId: 17 } }, Notes: secret }),
    } });
    await f.put(2, 'unknown', { form: tree, entity: 'unrestricted', operation: {
        kind: 'territory.move',
        data: JSON.stringify({ From: { ...from, Name: secret }, To: { ...to, Name: secret }, AttemptId: guid(102) }),
    } });

    assert.deepEqual(JSON.parse(JSON.stringify(await f.read(fact, 'workplace'))), [
        { attemptId: attempt(1), kind: 'confirm-location', data: JSON.stringify({ SiteId: 9, EquipmentId: 17 }) },
    ]);
    assert.deepEqual(JSON.parse(JSON.stringify(await f.read(tree, 'unrestricted'))), [
        { attemptId: attempt(2), kind: 'territory.move', data: JSON.stringify({ From: from, To: to }) },
    ]);
    assert.equal(f.records.size, 2);
});

test('territory rejects ambiguous nodes and mismatched operation identity', async () => {
    const form = 'territory-tree-move';
    const entity = 'unrestricted';
    const From = { Id: 4, ParentId: null, Left: 0, Right: 3 };
    for (const data of [
        { From, To: From },
        { From: { ...From, Right: 0 }, To: null },
        { From, To: null, AttemptId: guid(400) },
    ]) {
        const f = await fixture();
        await f.put(1, 'unknown', { form, entity, operation: { kind: 'territory.move', data: JSON.stringify(data) } });
        await assert.rejects(f.read(form, entity), /свер|прежн/i);
        assert.equal(f.records.size, 1);
    }
});

test('ModelEquipment and PhoneTemplate reject wrong kinds, IDs and noncanonical creation scope', async () => {
    const model = 'inventory.model-equipment';
    const template = 'inventory.phone-template';
    const stamp = null;
    const Fingerprint = 'd'.repeat(64);
    for (const [form, entity, kind, data] of [
        [model, '8', 'equipment-model.save', { Id: 9, ExpectedChangedAt: stamp, Fingerprint }],
        [model, 'new:2', 'equipment-model.save', { Id: 2, ExpectedChangedAt: stamp, Fingerprint }],
        [model, '8', 'equipment-model.save', { Id: 8, ExpectedChangedAt: stamp, Fingerprint: secret }],
        [model, '8', 'equipment-model.delete', { Id: 8, ExpectedChangedAt: stamp, Fingerprint }],
        [template, '8', 'equipment-phone-template.save', { Id: '8', ExpectedChangedAt: stamp }],
        [template, '8', 'equipment-phone-template.delete', { Id: 8, ExpectedChangedAt: stamp }],
    ]) {
        const f = await fixture();
        await f.put(1, 'unknown', { form, entity, operation: { kind, data: JSON.stringify(data) } });
        await assert.rejects(f.read(form, entity), /безопасно прочитать/);
        assert.equal(f.records.size, 1);
        assert.equal(f.held.size, 0);
    }

    const invalid = await fixture();
    await assert.rejects(invalid.read(model, 'new:08'), /Неверный контекст/);
    await assert.rejects(invalid.read(template, 'new'), /Неверный контекст/);
    await invalid.put(1, 'running', { form: model, entity: 'new:2147483648' });
    await assert.rejects(invalid.read(model, 'new'), /определить владельца/);
    assert.equal(invalid.records.size, 1);
});

test('SlBl projects the adapter operation ID and exact list scope without list contents', async () => {
    const f = await fixture();
    const form = 'slbl';
    const address = 'Alias@corp.example';
    const entity = address + ':1';
    const original = '\r\n'.repeat(3900);
    const desired = '\r\n'.repeat(3900) + secret;
    const OperationId = '2F935850-2CA6-4747-942F-432ADE6388EE';
    const data = JSON.stringify({ OperationId, Address: address, Kind: 1, Original: original,
        Desired: desired, Partial: secret, Status: 'confirmed' });
    assert.ok(data.length > 16_384);
    await f.put(1, 'confirmed', { form, entity, operation: { kind: 'slbl.sync', data } });
    await f.put(2, 'unknown', { form, entity, operation: { attemptId: guid(2), kind: 'slbl.sync', data } });
    await f.put(3, 'unknown', { form, entity: address + ':0', operation: { kind: 'slbl.sync', data: secret } });
    const lease = guid(602);

    try {
        const evidence = JSON.parse(JSON.stringify(await f.begin(lease, form, entity)));
        assert.deepEqual(evidence, [{
            attemptId: OperationId.replaceAll('-', '').toLowerCase(), kind: 'slbl.sync', data: entity,
        }]);
        assert.equal(await f.oldBegin(form, entity), false);
        assert.equal(await f.oldBegin(form, address + ':0'), true);
        assert.equal(f.records.size, 3);
    } finally {
        await f.finish(lease);
    }
    assert.equal(f.held.size, 0);
});

test('SlBl rejects mismatched mailbox or kind, malformed GUIDs and unsupported operation without rewriting drafts', async () => {
    const form = 'slbl';
    const entity = 'user@corp.example:0';
    const snapshot = { OperationId: attempt(700), Address: 'user@corp.example', Kind: 0,
        Original: secret, Desired: secret };
    for (const [payload, outer] of [
        [{ ...snapshot, Address: 'User@corp.example' }, {}],
        [{ ...snapshot, Kind: 1 }, {}],
        [{ ...snapshot, Kind: '0' }, {}],
        [{ ...snapshot, OperationId: guid(0) }, {}],
        [{ ...snapshot, OperationId: 'bad' }, {}],
        [snapshot, { attemptId: 'bad' }],
        [snapshot, { kind: 'slbl.update' }],
    ]) {
        const f = await fixture();
        await f.put(1, 'running', { form, entity,
            operation: { kind: 'slbl.sync', data: JSON.stringify(payload), ...outer } });
        await assert.rejects(f.read(form, entity), /безопасно прочитать/);
        assert.equal(f.records.size, 1);
        assert.equal(f.held.size, 0);
    }
    const f = await fixture();
    await assert.rejects(f.read(form, 'user@corp.example:2'), /Неверный контекст/);
    await assert.rejects(f.read(form, 'user@corp.example:00'), /Неверный контекст/);
});

test('Territory and PhoneCode creation aggregate checks only persisted aliases with identity-only projection', async () => {
    const f = await fixture();
    const territory = 'territory-form';
    const phone = 'phone-code-form';
    const rootTerritory = { Record: { Id: 0, ParentId: null, Name: secret } };
    const childTerritory = { Record: { Id: 0, ParentId: 19, Caption: secret } };
    const insert = { Original: { Code: '', Comment: secret }, Current: { Code: '001', TerritoryId: 19, Comment: secret } };
    for (const [id, form, entity, kind, snapshot] of [
        [1, territory, 'new:0', 'territory.create', { Original: rootTerritory, Current: rootTerritory }],
        [2, territory, 'new:19', 'territory.create', { Original: childTerritory, Current: childTerritory }],
        [3, phone, 'new:', 'phone-code.insert', insert],
        [4, phone, 'new:note|19', 'phone-code.insert', insert],
    ]) {
        await f.put(id, 'running', { form, entity, operation: { kind, data: JSON.stringify(snapshot) } });
    }
    const territoryLease = guid(741);
    const territoryEvidence = await f.begin(territoryLease, territory, 'new');
    try {
        assert.deepEqual(JSON.parse(JSON.stringify(territoryEvidence)), [
            { attemptId: attempt(1), kind: 'territory.create', data: '{"Id":0,"ParentId":null}' },
            { attemptId: attempt(2), kind: 'territory.create', data: '{"Id":0,"ParentId":19}' },
        ]);
        assert.equal(await f.oldBegin(territory, 'new:19'), false);
        assert.equal(await f.oldBegin(territory, 'new:29'), true);
    } finally { await f.finish(territoryLease); }

    const phoneLease = guid(742);
    const phoneEvidence = await f.begin(phoneLease, phone, 'new');
    try {
        assert.deepEqual(JSON.parse(JSON.stringify(phoneEvidence)), [3, 4].map(id => ({
            attemptId: attempt(id), kind: 'phone-code.insert', data: '{"Source":null,"Target":"001"}',
        })));
        assert.equal(await f.oldBegin(phone, 'new:note|19'), false);
        assert.ok(f.held.has('clay-draft:' + phone + ':new:note|19:' + guid(4)));
    } finally { await f.finish(phoneLease); }
    assert.equal(f.held.size, 0);
    assert.equal(f.records.size, 4);
});

function poolFields(number, links) {
    return { Data: { Header: { Number: number, Notes: secret }, Phones: links, Connections: [{ Id: 999, Name: secret }] },
        EmployeePrivate: true };
}

test('existing cards keep exact keys and NumPool emits only affected Tel identities', async () => {
    const f = await fixture();
    const number = '00001';
    const oldPool = poolFields(number, [{ Id: 11, Private: 0 }, { Id: 7, Private: 0 }, { Id: 9, Private: 0 }]);
    const newPool = poolFields(number, [{ Id: 7, Private: 0, Name: secret }, { Id: 5, Private: 0 }, { Id: 11, Private: 1 }]);
    const cases = [
        ['territory-form', '29', 'territory.update',
            { Original: { Record: { Id: 29, ParentId: 19 } }, Current: { Record: { Id: 29, ParentId: 19, Direction: secret } } },
            { Id: 29, ParentId: 19 }],
        ['phone-code-form', 'existing:007', 'phone-code.delete',
            { Original: { Code: '007' }, Current: { Code: '007', Comment: secret } },
            { Source: '007', Target: '007' }],
        ['sim-fmtn', '001(23)', 'save',
            { Original: { Prefix: '001(23)' }, Current: { Prefix: '00000000001', Description: secret } },
            { Source: '001(23)', Target: '00000000001' }],
        ['sim-fmtn', '00000000002', 'delete',
            { Original: { Prefix: '00000000002' }, Current: { Prefix: 'ignored edited value' } },
            { Source: '00000000002', Target: '00000000002' }],
        ['num-pool', 'id:' + number, 'numpool.save', { Original: oldPool, Current: newPool },
            { Number: number, PhoneIds: [5, 9, 11] }],
        ['num-pool', 'id:' + number, 'numpool.delete', { Original: oldPool, Current: newPool },
            { Number: number, PhoneIds: [7, 9, 11] }],
    ];
    for (const [index, [form, entity, kind, snapshot]] of cases.entries())
        await f.put(index + 1, 'confirmed', { form, entity, operation: { kind, data: JSON.stringify(snapshot) } });

    for (const [index, [form, entity, kind, , data]] of cases.entries()) {
        const actual = await f.read(form, entity);
        assert.deepEqual(JSON.parse(JSON.stringify(actual.find(item => item.attemptId === attempt(index + 1)))),
            { attemptId: attempt(index + 1), kind, data: JSON.stringify(data) });
    }
});

test('card projections reject scope changes and ambiguous related phone lists without modifying records', async () => {
    const number = '00001';
    const empty = poolFields(number, []);
    for (const [form, entity, kind, snapshot] of [
        ['territory-form', 'new:19', 'territory.create',
            { Original: { Record: { Id: 0, ParentId: 19 } }, Current: { Record: { Id: 0, ParentId: 20 } } }],
        ['phone-code-form', 'existing:007', 'phone-code.update', { Original: { Code: '007' }, Current: { Code: '7' } }],
        ['sim-fmtn', '001', 'save', { Original: { Prefix: '01' }, Current: { Prefix: '002' } }],
        ['sim-fmtn', 'new', 'delete', { Original: { Prefix: '' }, Current: { Prefix: '002' } }],
        ['num-pool', 'id:' + number, 'numpool.save',
            { Original: empty, Current: poolFields(number, [{ Id: 7, Private: 0 }, { Id: 7, Private: 1 }]) }],
        ['num-pool', 'id:' + number, 'numpool.save',
            { Original: empty, Current: poolFields(number, [{ Id: '7', Private: 0 }]) }],
        ['num-pool', 'id:' + number, 'numpool.save',
            { Original: empty, Current: { Data: { Header: { Number: number } } } }],
    ]) {
        const f = await fixture();
        await f.put(1, 'unknown', { form, entity, operation: { kind, data: JSON.stringify(snapshot) } });
        await assert.rejects(f.read(form, entity), /безопасно прочитать/);
        assert.equal(f.records.size, 1);
        assert.equal(f.held.size, 0);
    }
});

const equipmentForm = 'equipment-form';
const equipmentKey = '7';
const equipmentOwner = 'inventory:' + employee;
const exactEquipmentPeriodTicks = '639313362000035001';

function equipmentChildOperation(kind, editor, suffix, intent, equipmentId = 7) {
    return {
        kind,
        data: JSON.stringify({ Origin: 'child', Kind: kind, EquipmentId: equipmentId, Editor: editor, Intent: intent }),
        scopes: [{ name: 'child:' + editor + ':' + suffix + ':root', typedRevision: 5, rawRevisions: {} }],
    };
}

async function putEquipmentEvidence(f, id, operation, options = {}) {
    await f.put(id, 'unknown', { form: equipmentForm, entity: equipmentKey, userKey: equipmentOwner, operation, ...options });
}

test('EquipmentForm requires its exact inventory owner in both storage and encrypted payload', async () => {
    const f = await fixture({ keyOwner: equipmentOwner });
    const kind = 'equipment-save';
    const operation = {
        kind,
        data: JSON.stringify({ Origin: 'main', Kind: kind, EquipmentId: 7, Editor: null, Intent: { Notes: secret } }),
        scopes: [{ name: 'main' }],
    };
    await putEquipmentEvidence(f, 1, operation);
    await putEquipmentEvidence(f, 2, operation, { userKey: employee });
    await putEquipmentEvidence(f, 3, operation, { userKey: 'inventory:28' });
    assert.deepEqual(JSON.parse(JSON.stringify(await f.read(equipmentForm, equipmentKey, equipmentOwner))), [
        { attemptId: attempt(1), kind, data: '{}' },
    ]);
    const calls = f.calls.length;
    for (const owner of [employee, 'inventory:0' + employee, 'Inventory:' + employee, 'inventory:' + employee + ':other'])
        await assert.rejects(f.read(equipmentForm, equipmentKey, owner), /Неверный контекст/);
    await assert.rejects(f.read('maintenance', 'list', equipmentOwner), /Неверный контекст/);
    assert.equal(f.calls.length, calls);
    assert.equal(f.records.size, 3);

    const mismatched = await fixture({ keyOwner: equipmentOwner });
    await putEquipmentEvidence(mismatched, 1, operation, { ownerKey: 'inventory:28' });
    await assert.rejects(mismatched.read(equipmentForm, equipmentKey, equipmentOwner), /подтвердить владельца/);
    assert.equal(mismatched.records.size, 1);
    assert.equal(mismatched.held.size, 0);
});

test('EquipmentForm projects no legacy account or SIM secrets and preserves only a modern binding', async () => {
    const f = await fixture({ keyOwner: equipmentOwner });
    const binding = 'ab'.repeat(32);
    const operations = [
        ['Добавление учётной записи', 'EquipmentAccountDialog', 'new',
            { Login: 'fixture-private-login', Password: 'fixture-private-password', Notes: secret }, '{}'],
        ['Сохранение SIM', 'EquipmentSimDialog', 'new',
            { PIN: 'fixture-pin', PIN2: 'fixture-pin2', PUK: 'fixture-puk', PUK2: 'fixture-puk2',
                PhoneNumber: 'fixture-private-phone', SecretProof: secret }, '{}'],
        ['equipment-account.insert', 'EquipmentAccountDialog', 'new', { Binding: binding }, JSON.stringify({ Binding: binding })],
        ['equipment-sim.update', 'EquipmentSimDialog', 'new', { DuplicateEquipmentId: 8 }, '{"DuplicateEquipmentId":8}'],
    ];
    for (const [index, [kind, editor, suffix, intent]] of operations.entries())
        await putEquipmentEvidence(f, index + 1, equipmentChildOperation(kind, editor, suffix, intent));
    const lease = guid(301);
    const result = await f.begin(lease, equipmentForm, equipmentKey, equipmentOwner);
    try {
        assert.deepEqual(JSON.parse(JSON.stringify(result)), operations.map(([kind, , , , data], index) =>
            ({ attemptId: attempt(index + 1), kind, data })));
        assert.doesNotMatch(JSON.stringify(result), /fixture-private|fixture-pin|fixture-puk|SecretProof|secret|typed|raw/);
        assert.equal(await f.oldBegin(equipmentForm, equipmentKey, equipmentOwner), false);
        assert.equal(f.records.size, operations.length);
    } finally {
        await f.finish(lease);
    }
    assert.equal(f.held.size, 0);
});

test('EquipmentForm period keys preserve exact decimal ticks larger than JS safe integers', async () => {
    const f = await fixture({ keyOwner: equipmentOwner });
    const ticks = exactEquipmentPeriodTicks;
    assert.notEqual(String(Number(ticks)), ticks);
    const cases = [
        ['equipment-location-period.update', 'EquipmentLocationDialog', ticks],
        ['equipment-responsible-period.withdraw', 'EquipmentResponsibleDialog', 'Withdraw:' + ticks],
    ];
    for (const [index, [kind, editor, suffix]] of cases.entries()) {
        const operation = equipmentChildOperation(kind, editor, suffix, { FromUtcTicks: 'numeric-placeholder' });
        operation.data = operation.data.replace('"numeric-placeholder"', ticks);
        await putEquipmentEvidence(f, index + 1, operation);
    }

    assert.deepEqual(JSON.parse(JSON.stringify(await f.read(equipmentForm, equipmentKey, equipmentOwner))),
        cases.map(([kind], index) => ({ attemptId: attempt(index + 1), kind, data: JSON.stringify({ FromUtcTicks: ticks }) })));
    assert.equal(f.records.size, cases.length);
    assert.equal(f.held.size, 0);
});

test('EquipmentForm rejects ambiguous or secret-bearing modern metadata without touching its draft', async () => {
    const binding = 'ab'.repeat(32);
    const misplacedTicks = equipmentChildOperation('equipment-location-period.update', 'EquipmentLocationDialog',
        String(Number(exactEquipmentPeriodTicks)), { FromUtcTicks: 'numeric-placeholder' });
    misplacedTicks.data = misplacedTicks.data.replace('"numeric-placeholder"', exactEquipmentPeriodTicks);
    const duplicateTicks = equipmentChildOperation('equipment-location-period.update', 'EquipmentLocationDialog',
        exactEquipmentPeriodTicks, { FromUtcTicks: 'numeric-placeholder' });
    duplicateTicks.data = duplicateTicks.data.replace('"numeric-placeholder"',
        exactEquipmentPeriodTicks + ',"FromUtcTicks":' + exactEquipmentPeriodTicks);
    const cases = [
        equipmentChildOperation('equipment-account.insert', 'EquipmentAccountDialog', 'new', { Binding: binding, Password: secret }),
        equipmentChildOperation('equipment-account.insert', 'EquipmentAccountDialog', 'new', { Binding: binding.toUpperCase() }),
        equipmentChildOperation('equipment-account.insert', 'EquipmentSimDialog', 'new', { Binding: binding }),
        equipmentChildOperation('equipment-account.insert', 'EquipmentAccountDialog', 'new', { Binding: binding }, 8),
        equipmentChildOperation('equipment-sim.update', 'EquipmentSimDialog', 'new', { DuplicateEquipmentId: 8, PUK: secret }),
        equipmentChildOperation('equipment-sim.update', 'EquipmentSimDialog', 'new', { DuplicateEquipmentId: 7 }),
        misplacedTicks, duplicateTicks,
    ];
    for (const operation of cases) {
        const f = await fixture({ keyOwner: equipmentOwner });
        await putEquipmentEvidence(f, 1, operation);
        await assert.rejects(f.begin(guid(302), equipmentForm, equipmentKey, equipmentOwner), /безопасно прочитать/);
        assert.equal(f.records.size, 1);
        assert.equal(f.held.size, 0);
    }
});
