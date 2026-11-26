import assert from 'node:assert/strict';
import { webcrypto } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

const script = await readFile(new URL('../../src/Clayzor.Lib.Web.Controls/wwwroot/js/claySelectAdvancedSearch.js', import.meta.url), 'utf8');
const requestId = '1234567890abcdef1234567890abcdef';
const secondId = 'abcdef1234567890abcdef1234567890';
const parentOrigin = 'https://parent.example';
const childOrigin = 'https://directory.example';

function browser(href) {
    const callbacks = new Set();
    const timeoutCallbacks = new Set();
    const intervalCallbacks = new Set();
    const win = {
        location: new URL(href),
        closed: false,
        close() { this.closed = true; },
        addEventListener(_, callback) { callbacks.add(callback); },
        removeEventListener(_, callback) { callbacks.delete(callback); },
        setTimeout(callback) { timeoutCallbacks.add(callback); return callback; },
        clearTimeout(callback) { timeoutCallbacks.delete(callback); },
        setInterval(callback) { intervalCallbacks.add(callback); return callback; },
        clearInterval(callback) { intervalCallbacks.delete(callback); },
        message(event) { return Promise.all([...callbacks].map(listener => listener(event))); },
        timeOut() { for (const callback of [...timeoutCallbacks]) callback(); },
        tick() { for (const callback of [...intervalCallbacks]) callback(); },
    };
    vm.runInNewContext(script, { window: win, document: { referrer: parentOrigin }, URL, URLSearchParams, crypto: webcrypto });
    return win;
}

function transport(invokeMethodAsync) {
    const parent = browser(parentOrigin);
    const children = new Map();
    parent.clayFormWindow = {
        openHandle(href) {
            const child = browser(href);
            child.opener = parent;
            child.postMessage = (data, origin) => {
                assert.equal(origin, childOrigin);
                if (!child.dropAck)
                    void child.message({ origin: parentOrigin, source: parent, data });
            };
            children.set(child.location.searchParams.get('requestId'), child);
            return child;
        },
    };
    const open = (id, multiple = false) => {
        parent.claySelectAdvancedSearch.open(childOrigin + '/search.aspx', id, { invokeMethodAsync }, multiple, true);
        return children.get(id);
    };
    const choose = (child, id, value) => {
        parent.postMessage = (data, origin) => {
            child.delivery = data;
            assert.equal(origin, parentOrigin);
            void parent.message({ origin: childOrigin, source: child, data });
        };
        return child.claySelectAdvancedSearch.returnValue(id, value);
    };
    return { parent, open, choose };
}

export async function runAdvancedSearchFixture() {
    const calls = [];
    const { parent, open, choose } = transport(async (method, id, revision, value) => {
        calls.push({ method, id, revision, value });
        if (method === 'CancelAsync') return;
        if (value === 11) return 'Rejected';
        if (value === 99) return 'Stale';
        if (value === 51) throw new Error('Disconnected');
        return 'Accepted';
    });

    const child = open(requestId);
    const message = { type: 'clay-select-return', requestId, value: 42, revision: 1, receipt: requestId };
    await parent.message({ origin: 'https://foreign.example', source: child, data: message });
    await parent.message({ origin: childOrigin, source: {}, data: message });
    await parent.message({ origin: childOrigin, source: child, data: { ...message, requestId: secondId } });
    await parent.message({ origin: childOrigin, source: child, data: { ...message, value: [] } });
    assert.equal(calls.length, 0);

    assert.equal(await choose(child, requestId, 11), false);
    assert.equal(child.closed, false);
    assert.ok(parent.claySelectAdvancedSearch.requests[requestId]);
    assert.equal(await choose(child, requestId, 42), true);
    assert.equal(child.closed, true);
    assert.deepEqual(calls.map(call => call.revision), [1, 2]);
    parent.tick();
    assert.equal(calls.at(-1).method, 'CancelAsync');

    const lost = open(requestId, true);
    const stale = open(secondId);
    lost.dropAck = true;
    const first = choose(lost, requestId, []);
    await Promise.resolve();
    lost.timeOut();
    assert.equal(await first, false);
    assert.equal(lost.closed, false);
    assert.equal(await choose(lost, requestId, [123]), false);
    lost.dropAck = false;
    assert.equal(await choose(lost, requestId, []), true);
    const empties = calls.filter(call => Array.isArray(call.value));
    assert.deepEqual(empties.map(call => call.revision), [1, 1]);
    assert.equal(empties[0].value.length, 0);
    assert.equal(await choose(stale, secondId, 99), false);
    assert.equal(stale.closed, false);

    const disconnected = open(requestId);
    assert.equal(await choose(disconnected, requestId, 51), false);
    assert.equal(disconnected.closed, false);
    disconnected.location.hash = '#changed';
    parent.closed = true;
    assert.equal(await choose(disconnected, requestId, 51), false);
    parent.closed = false;

    let release;
    const guarded = transport(() => new Promise(resolve => { release = resolve; }));
    const guardedChild = guarded.open(requestId);
    const awaiting = guarded.choose(guardedChild, requestId, 12);
    const ack = { type: 'clay-select-ack', requestId, revision: 1, receipt: guardedChild.delivery.receipt, decision: 'Accepted' };
    await guardedChild.message({ origin: 'https://foreign.example', source: guarded.parent, data: ack });
    await guardedChild.message({ origin: parentOrigin, source: {}, data: ack });
    await guardedChild.message({ origin: parentOrigin, source: guarded.parent, data: { ...ack, revision: 2 } });
    await guardedChild.message({ origin: parentOrigin, source: guarded.parent, data: { ...ack, receipt: secondId } });
    assert.equal(guardedChild.closed, false);
    guardedChild.location.hash = '#new-context';
    release('Accepted');
    await Promise.resolve();
    guardedChild.timeOut();
    assert.equal(await awaiting, false);
    assert.equal(guardedChild.closed, false);

    let responses = 0;
    let releaseLate;
    const overlap = transport(() => {
        responses++;
        return responses === 1 ? new Promise(resolve => { releaseLate = resolve; }) : Promise.resolve('Accepted');
    });
    const overlapChild = overlap.open(requestId);
    const uncertain = overlap.choose(overlapChild, requestId, 18);
    overlapChild.timeOut();
    assert.equal(await uncertain, false);
    assert.equal(await overlap.choose(overlapChild, requestId, 18), true);
    releaseLate('Rejected');
    await Promise.resolve();
    assert.equal(overlapChild.closed, true);

    parent.clayFormWindow.openHandle = () => null;
    open(secondId);
    assert.equal(calls.at(-1).method, 'CancelAsync');
    return 'origin/source/request, rejected retry, lost ack, empty multiple, stale context, disconnect, blocked popup';
}

await runAdvancedSearchFixture();
