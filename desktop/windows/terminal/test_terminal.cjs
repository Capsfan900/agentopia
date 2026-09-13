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
const contextMessage = {
  kind: 'sessionContext', panes: [{
    paneId: uuid, relation: 'current', sessionId: generation,
    workingOn: 'Implement safe parser', workingSource: 'agent_message', status: 'working',
    turnStatus: 'inProgress', approvalPending: true, stale: false,
    updated: '2026-09-13T12:00:00-04:00',
    handoff: { state: 'saved', itemId: 'work-1', revision: 'a'.repeat(64), capturedAt: '2026-09-13T11:00:00-04:00', archived: false, completion: { completed: true, source: 'user', trusted: true } }
  }]
};
const paneState = new Map([[uuid, { id: uuid, terminal: { marker: true } }]]);
const retainedTerminal = paneState.get(uuid).terminal;
assert.equal(terminal.applySessionContext(paneState, contextMessage), true);
assert.equal(paneState.get(uuid).terminal, retainedTerminal);
assert.match(terminal.contextText(paneState.get(uuid).sessionContext), /Implement safe parser/);
assert.match(terminal.contextText(paneState.get(uuid).sessionContext), /Approval required/);
assert.match(terminal.contextText(paneState.get(uuid).sessionContext), /Saved handoff/);
assert.equal(terminal.applySessionContext(paneState, { kind: 'sessionContext', panes: [{ paneId: 'bad', relation: 'current' }] }), false);
assert.match(terminal.contextText({ relation: 'source', workingOn: 'Original work', status: 'completed', handoff: { state: 'none' } }), /fork reference/);
assert.equal(terminal.contextText({ relation: 'unlinked' }), 'Shell — no agent session linked.');
assert.equal(terminal.contextText({ relation: 'unavailable' }), 'Session context unavailable.');
const earlyContexts = new Map();
assert.equal(terminal.applySessionContext(new Map(), contextMessage, earlyContexts), true);
assert.equal(earlyContexts.get(uuid).workingOn, 'Implement safe parser');
const drainingExit = { status: 'exited', socket: { marker: true }, attachment: { marker: true }, terminal: { options: { disableStdin: false } } };
terminal.observeExit(drainingExit);
assert.equal(drainingExit.terminal.options.disableStdin, true);
assert.ok(drainingExit.socket && drainingExit.attachment, 'observing exit must not cancel final output drainage');

console.log('terminal client checks passed');
