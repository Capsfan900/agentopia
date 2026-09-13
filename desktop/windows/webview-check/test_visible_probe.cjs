// Run the actual checker probe without opening a WebView or changing production code.
const fs = require('node:fs'), vm = require('node:vm'), assert = require('node:assert/strict');
const source = fs.readFileSync(__dirname + '/VisibleCheck.cs', 'utf8');
const script = source.match(/const string StartVisibleProbe = """\r?\n([\s\S]*?)\r?\n\s*""";/)[1];
const context = vm.createContext({
  document: {focused: true, hidden: false, hasFocus() {return this.focused;}},
  window: {addEventListener() {}, removeEventListener() {}},
  paused: false, reducedMotion: {matches: false}, renderedAt: 100,
  motionActive() {return context.document.focused && !context.document.hidden && !context.paused;},
  workers: new Map(Array.from({length: 24}, (_, i) => [i, {}])),
  canvas: {clientWidth: 892, clientHeight: 875}, devicePixelRatio: 1,
  performance: {now: () => 100}, requestAnimationFrame: () => 1, cancelAnimationFrame() {}
});
vm.runInContext(script, context);
context.document.focused = false; context.document.hidden = true;
context.paused = true; context.reducedMotion.matches = true;
const output = JSON.parse(JSON.stringify(context.window.__foundryVisibleProbe.stop()));
assert.deepEqual(output.start, {document_has_focus: true, document_hidden: false,
  paused: false, reduced_motion: false, motion_active: true});
assert.deepEqual(output.end, {document_has_focus: false, document_hidden: true,
  paused: true, reduced_motion: true, motion_active: false});
console.log('VISIBLE_PROBE: actual probe emits distinct start/end focus, hidden, pause, reduced-motion and eligibility values');
const overrideMatch = source.match(/const string EnableVisibleMotion = """\r?\n([\s\S]*?)\r?\n\s*""";/);
assert.ok(overrideMatch, 'synthetic test must explicitly enable page-only motion and report before/after eligibility');
context.document.focused = true; context.document.hidden = false;
context.motionLabel = () => {}; context.paint = () => {};
const override = JSON.parse(JSON.stringify(vm.runInContext(overrideMatch[1], context)));
assert.equal(override.before.paused, true);
assert.equal(override.before.reduced_motion, true);
assert.equal(override.after.paused, false);
assert.equal(override.after.reduced_motion, true);
assert.equal(override.after.motion_active, true);
assert.equal(context.paused, false);
assert.equal(context.reducedMotion.matches, true);
console.log('VISIBLE_MOTION_OVERRIDE: actual script unpauses only this page and preserves the reduced-motion preference');
const readinessScript = source.match(/const string ReadVisibleEligibility = """\r?\n([\s\S]*?)\r?\n\s*""";/)[1];
assert.deepEqual(JSON.parse(JSON.stringify(vm.runInContext(readinessScript, context))), {
  document_has_focus: true, document_hidden: false, paused: false, reduced_motion: true, motion_active: true
});
console.log('VISIBLE_READINESS_SCRIPT: exact expression returns the expected object after override; normal-context check only, not an attempt-05 reproduction');
const contextScript = source.match(/const string ReadVisibleContext = """\r?\n([\s\S]*?)\r?\n\s*""";/)[1];
context.location = {origin: 'http://127.0.0.1:12345', pathname: '/observatory', search: '?private=not-for-logs'};
context.document.readyState = 'complete';
context.crypto = {randomUUID: () => 'first-document'};
const firstContext = JSON.parse(JSON.stringify(vm.runInContext(contextScript, context)));
assert.deepEqual(firstContext, {url: 'http://127.0.0.1:12345/observatory', ready: 'complete', marker: 'first-document'});
context.crypto.randomUUID = () => 'next-document';
assert.equal(vm.runInContext(contextScript, context).marker, 'first-document');
delete context.window.__foundryVisibleContext;
assert.equal(vm.runInContext(contextScript, context).marker, 'next-document');
console.log('VISIBLE_CONTEXT_SCRIPT: document marker remains stable within its context and resets with the context; URL omits query data');
