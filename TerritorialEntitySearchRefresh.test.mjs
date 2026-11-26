import assert from "node:assert/strict";
import { test } from "node:test";
import { refreshMetadataFrame } from "../../src/Kesco.App.Web.Inventory/wwwroot/js/territorialEntitySearch.js";

const url = "https://inventory.test/?id=1030&Parent=eq%7E5";

test("updates the existing grid and waits for its acknowledgement", async () => {
    let resolve;
    let refreshes = 0;
    const child = {
        location: { href: url },
        refresh() {
            assert.equal(this, child);
            refreshes++;
            return new Promise(callback => { resolve = callback; });
        }
    };
    const frame = { isConnected: true, contentWindow: child, src: url };
    const pending = refreshMetadataFrame(frame, url);
    let acknowledged = false;
    pending.then(() => { acknowledged = true; });
    await Promise.resolve();
    assert.equal(acknowledged, false);
    assert.equal(refreshes, 1);
    resolve();
    assert.equal(await pending, true);
    assert.equal(frame.src, url);
    assert.equal(frame.contentWindow, child);
});

test("skips detached frames, different contexts and unready grids", async () => {
    const refresh = () => assert.fail("Unexpected metadata refresh");
    const child = { location: { href: url }, refresh };
    assert.equal(await refreshMetadataFrame({ isConnected: false, contentWindow: child }, url), false);
    assert.equal(await refreshMetadataFrame({ isConnected: true, contentWindow: child }, url + "2"), false);
    assert.equal(await refreshMetadataFrame({ isConnected: true, contentWindow: { location: { href: url } } }, url), false);
});

test("propagates callback rejection to the search component", async () => {
    const expected = new Error("circuit disconnected");
    const child = { location: { href: url }, refresh: () => Promise.reject(expected) };
    await assert.rejects(refreshMetadataFrame({ isConnected: true, contentWindow: child }, url), expected);
});
