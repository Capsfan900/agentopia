const assert = require('node:assert/strict');
const terminal = require('./terminal.js');

const uuid = '123e4567-e89b-42d3-a456-426614174000';
const generation = '123e4567-e89b-42d3-a456-426614174001';

assert.equal(terminal.byteLength('€'.repeat(2730)), 8190);
assert.equal(terminal.isInputFrame({ kind: 'input', data: '€'.repeat(2731) }), false);
assert.equal(terminal.isInputFrame({ kind: 'input', data: 'ok' }), true);
assert.equal(terminal.isResizeFrame({ kind: 'resize', columns: 2, rows: 300 }), true);
assert.equal(terminal.isResizeFrame({ kind: 'resize', columns: 501, rows: 24 }), false);
assert.equal(terminal.ticketBody(uuid, generation), JSON.stringify({ paneId: uuid, generation }));
assert.throws(() => terminal.ticketBody(uuid, 7), /UUID/);
const registered = [];
terminal.registerBlockedOscHandlers({ registerOscHandler(code, handler) { registered.push([code, handler]); } });
assert.deepEqual(registered.map(([code]) => code), [0, 1, 2, 52]);
assert.equal(registered.every(([, handler]) => handler() === true), true);
const split = terminal.moveIntoSplit([
  { id: 'one', paneIds: ['first'], orientation: 'horizontal' },
  { id: 'two', paneIds: ['second'], orientation: 'horizontal' }
], 'one', 'second', 'vertical');
assert.deepEqual(split.tabs, [{ id: 'one', paneIds: ['first', 'second'], orientation: 'vertical' }]);
assert.deepEqual(terminal.unsplitTab(split.tabs, 'one', 'three').tabs, [
  { id: 'one', paneIds: ['first'], orientation: 'vertical' },
  { id: 'three', paneIds: ['second'], orientation: 'horizontal' }
]);
assert.equal(terminal.outboundError({ kind: 'input', data: 'ok' }, 512 * 1024), 'backpressure');
assert.equal(terminal.outboundError({ kind: 'input', data: 'ok' }, 0), '');
assert.equal(terminal.nextOutputBytes(512 * 1024 - 1, 1), 512 * 1024);
assert.equal(terminal.nextOutputBytes(512 * 1024, 1), -1);
const retainedQueue = terminal.nextOutputBytes(0, 400 * 1024);
assert.equal(terminal.nextOutputBytes(retainedQueue, 200 * 1024), -1);
assert.equal(terminal.releaseOutputBytes(retainedQueue, 400 * 1024), 0);
let refresh = terminal.advanceRefresh({ inflight: false, pending: false }, 'request');
assert.deepEqual(refresh, { state: { inflight: true, pending: false }, run: true });
refresh = terminal.advanceRefresh(refresh.state, 'request');
assert.deepEqual(refresh, { state: { inflight: true, pending: true }, run: false });
refresh = terminal.advanceRefresh(refresh.state, 'complete');
assert.deepEqual(refresh, { state: { inflight: true, pending: false }, run: true });
assert.equal(terminal.safeText('<img src=x onerror=alert(1)>'), '<img src=x onerror=alert(1)>');

console.log('terminal client checks passed');
