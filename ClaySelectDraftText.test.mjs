import assert from 'node:assert/strict';
import test from 'node:test';
import vm from 'node:vm';
import { readFile } from 'node:fs/promises';

const script = await readFile(new URL('../../src/Clayzor.Lib.Web.Controls/wwwroot/js/claySelect.js', import.meta.url), 'utf8');

function createSelect() {
    const window = {};
    vm.runInNewContext(script, { window });
    return window.claySelect;
}

test('draft text requires a connected ClaySelect and acknowledgement from its component', async () => {
    const select = createSelect();
    assert.equal(await select.restoreDraftText('field', 'old draft'), false);

    const input = { value: 'new value', disabled: false, isConnected: true };
    select.instances.field = { input, dotnetRef: { invokeMethodAsync: async () => false } };
    assert.equal(await select.restoreDraftText('field', 'old draft'), false);
    assert.equal(input.value, 'new value');

    select.instances.field.dotnetRef.invokeMethodAsync = async (method, text) => {
        assert.equal(method, 'RestoreDraftTextAsync');
        assert.equal(text, 'old draft');
        return true;
    };
    assert.equal(await select.restoreDraftText('field', 'old draft'), true);
    assert.equal(input.value, 'old draft');
});

test('ClaySelect preserves newer DOM input while old draft acknowledgement is in flight', async () => {
    const select = createSelect();
    const input = { value: 'initial', disabled: false, isConnected: true };
    let accept;
    const response = new Promise(resolve => { accept = resolve; });
    select.instances.field = { input, dotnetRef: { invokeMethodAsync: () => response } };
    const restoring = select.restoreDraftText('field', 'old draft');
    input.value = 'newer edit';
    accept(true);

    assert.equal(await restoring, true);
    assert.equal(input.value, 'newer edit');
});
