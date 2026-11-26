import assert from "node:assert/strict";
import { test } from "node:test";
import { clearMoveMarker } from "../../src/Kesco.App.Web.Inventory/wwwroot/js/territorialEntitySearch.js";

const key = "territory-move:4";
const marker = '{"AttemptId":"original","From":{"Id":8},"To":{"Id":9}}';

test("clears exactly the confirmed marker without affecting other keys", () => {
    const stored = new Map([[key, marker], ["territory-move:6", marker]]);
    globalThis.sessionStorage = storage(stored);
    assert.equal(clearMoveMarker(key, marker), true);
    assert.equal(stored.has(key), false);
    assert.equal(stored.get("territory-move:6"), marker);
});

test("treats an already missing marker as a successful cleanup", () => {
    const stored = new Map();
    globalThis.sessionStorage = storage(stored);
    assert.equal(clearMoveMarker(key, marker), true);
});

test("preserves a replacement attempt even when its source and target are the same", () => {
    const replacement = marker.replace("original", "replacement");
    const stored = new Map([[key, replacement]]);
    globalThis.sessionStorage = storage(stored);
    assert.equal(clearMoveMarker(key, marker), false);
    assert.equal(stored.get(key), replacement);
});

test("propagates a storage failure so the form can retain the proven outcome", () => {
    const stored = new Map([[key, marker]]);
    globalThis.sessionStorage = {
        getItem: value => stored.get(value),
        removeItem: () => { throw new Error("storage not available"); }
    };
    assert.throws(() => clearMoveMarker(key, marker), /storage not available/);
    assert.equal(stored.get(key), marker);
});

function storage(stored) {
    return {
        getItem: key => stored.get(key) ?? null,
        removeItem: key => stored.delete(key)
    };
}
