// Run Operations' real grouping and semantic projection without browser dependencies.
const fs = require('node:fs'), vm = require('node:vm'), assert = require('node:assert/strict');
const elements = new Map();
function element(key) {
  if (!elements.has(key)) elements.set(key, {
    dataset: {}, className: '', hidden: false, innerHTML: '', textContent: '', children: [],
    classList: { toggle() {} }, addEventListener() {}, setAttribute() {}, append(child) { this.children.push(child) },
    prepend(child) { this.children.unshift(child) }, querySelectorAll() { return [] },
  });
  return elements.get(key);
}
const sandbox = vm.createContext({
  console, Map, Set, Math, JSON, Number, String, Date, Array, Object, Intl,
  document: { querySelector: element, querySelectorAll: () => [], createElement: () => element(Symbol()), activeElement: null },
  EventSource: class { addEventListener() {} }, requestAnimationFrame() {}, setTimeout() {}, setInterval() {},
});
vm.runInContext(fs.readFileSync('index.html', 'utf8').split('<script>')[1].split('</script>')[0], sandbox);
const run = expression => vm.runInContext(expression, sandbox);
function check(expression, message = expression) { assert.equal(run(expression), true, message) }
const failures = [];
function regression(name, body) { try { body() } catch (error) { console.error(name + ': ' + error.message); failures.push(error) } }

regression('Canonical projects combine Windows roots while preserving session ancestry', () => {
  sandbox.windowsA = '\\\\?\\C:\\Work\\Project\\'; sandbox.windowsB = 'c:/work/project';
  sandbox.uncA = '\\\\Server\\Share\\Project'; sandbox.uncB = '//server/share/project/';
  sandbox.opsFixture = { sessions: [
    { id: 'one', cwd: '\\\\?\\C:\\Work\\Project', status: 'working', working_on: { summary: 'Review release', status: 'working' } },
    { id: 'child', parent: 'one', status: 'idle', task: 'Wait for review' },
    { id: 'two', cwd: 'c:/work/project/', status: 'completed', prompt_context: { summary: 'Old prompt' } },
    { id: 'unknown', status: 'idle' },
  ] };
  check(`canonicalCwd(windowsA)===canonicalCwd(windowsB)`);
  check(`canonicalCwd('/Work/Project')!==canonicalCwd('/work/project')`, 'POSIX project paths remain case-sensitive');
  check(`canonicalCwd(uncA)===canonicalCwd(uncB)`, 'Windows UNC projects normalize case and separators without losing the share root');
  check(`(()=>{const groups=projectGroups(opsFixture.sessions);return groups.length===2&&groups[0].sessions.length===2&&groups[0].rows.some(s=>s.id==='child')&&groups[1].sessions[0].id==='unknown'})()`, 'Roots sharing a recorded cwd form one project; unknown roots remain separate');
  check(`(()=>{const group=projectGroups(opsFixture.sessions)[0];return group.sessions.every(root=>threadTitle(root,group)==='Project')})()`, 'Every root thread title uses the project folder while children remain assignments');
  check(`(()=>{const before=projectGroups(opsFixture.sessions).map(g=>g.key).join('|');return projectGroups(opsFixture.sessions.slice().reverse()).map(g=>g.key).sort().join('|')===before.split('|').sort().join('|')})()`, 'Project identity survives reordered events');
  run(`cycleGroups=projectGroups([{id:'cycle-b',parent:'cycle-a',cwd:'C:/Cycle'},{id:'cycle-a',parent:'cycle-b',cwd:'c:/cycle'}])`);
  check(`cycleGroups.length===1&&cycleGroups[0].sessions[0].id==='cycle-a'`, 'Malformed parent cycles choose one deterministic root');
});

regression('Operational state and project summaries use current recorded work only', () => {
  check(`JSON.stringify([{status:'idle'},{status:'completed'},{status:'running'},{status:'tool'},{status:'idle',approval_pending:true},{status:'working',current_action:'Waiting for delegated agents'}].map(operationalStatus))===JSON.stringify(['idle','completed','working','tool','approval','waiting'])`);
  run(`summaryFixture={sessions:[{id:'r',cwd:'C:/x',status:'working',prompt_context:{summary:'LATEST PROMPT'},random:'SECRET_JSON'},{id:'a',parent:'r',status:'working',task:'Audit UI',working_on:{summary:'Group projects',status:'working'}}]}`);
  check(`(()=>{const text=projectGroups(summaryFixture.sessions)[0].summary;return text.includes('Group projects')&&!text.includes('LATEST PROMPT')&&!text.includes('SECRET_JSON')})()`, 'Project headline rolls up active assignments/working anchors, not arbitrary payload or prompt text');
});

regression('Terminal turn state and project attention determine the operational order', () => {
  check(`JSON.stringify([
    {status:'idle',turn_status:'completed'},
    {status:'idle',turn_status:'failed'},
    {status:'idle',turn_status:'interrupted'},
    {status:'working',turn_status:'completed'},
    {status:'idle',turn_status:'completed',working_on:{status:'working'}},
    {status:'idle',turn_status:'completed',approval_pending:true},
    {status:'idle',current_action:'Waiting for another turn'}].map(operationalStatus))===JSON.stringify(['completed','failed','failed','working','working','approval','idle'])`, 'Terminal turn state wins unless an explicit current approval or active state supersedes it');
  run(`orderedProjects=projectGroups([
    {id:'inactive-z',cwd:'C:/Zeta',status:'idle'},
    {id:'active-z',cwd:'C:/Active',status:'working'},
    {id:'approval-z',cwd:'C:/Review',status:'idle',approval_pending:true},
    {id:'completed-a',cwd:'C:/Archive',status:'idle',turn_status:'completed'},
    {id:'inactive-a',cwd:'C:/Backlog',status:'idle'}])`);
  check(`JSON.stringify(orderedProjects.map(group=>projectName(group.cwd)))===JSON.stringify(['Review','Active','Archive','Backlog','Zeta'])`, 'Approval projects lead active projects; inactive projects follow by deterministic name');
});

regression('Current work tree follows observation identity and malformed cycles stay bounded', () => {
  run(`latest={sessions:[{id:'semantic',status:'working',prompt_context:{summary:'Audit operations',text:'<b>safe prompt</b>',source:'user_prompt'},working_on:{summary:'Implement grouping',status:'working',observation_id:'now'},work_breakdown:[{id:'old',kind:'step',summary:'Historical commentary',status:'working'},{id:'now',parent_id:'old',kind:'step',summary:'Implement grouping',status:'working'},{id:'cycle-a',parent_id:'cycle-b',kind:'step',summary:'Cycle A'},{id:'cycle-b',parent_id:'cycle-a',kind:'step',summary:'Cycle B'}],trace:{observations:{bad:true}}}]}`);
  run(`semanticHtml=semanticView(latest.sessions[0])`);
  check(`semanticHtml.includes('current-work')&&semanticHtml.indexOf('current-work')<semanticHtml.indexOf('Implement grouping')`, 'Only the observation referenced by working_on is marked current');
  check(`!semanticHtml.includes('current-work\">Historical commentary')&&semanticHtml.includes('Cycle A')`, 'Historical working commentary is not promoted and cyclic nodes render safely');
  check(`semanticHtml.includes('&lt;b&gt;safe prompt&lt;/b&gt;')&&!semanticHtml.includes('<b>safe prompt</b>')`, 'Prompt disclosure preserves escaping');
});

regression('Semantic work stays bounded and evidence is grouped by operator question', () => {
  run(`latest={sessions:[{id:'grouped',status:'working',working_on:{summary:'Run checks',observation_id:'now',status:'working'},work_breakdown:[{id:'root',kind:'prompt',summary:'Ship audit',session_id:'grouped',status:'working'},{id:'now',parent_id:'root',kind:'step',summary:'Run checks',session_id:'worker-1',status:'working'},{id:'old',parent_id:'root',kind:'outcome',summary:'Old result',session_id:'worker-2',status:'completed'}],trace:{observations:[{id:'approval',kind:'approval',summary:'Needs permission'},{id:'test',kind:'test',summary:'node tests',exit_code:0},{id:'file',kind:'file',summary:'index.html'},{id:'tool',kind:'tool',tool:'node'},{id:'metric',kind:'metric',summary:'33ms'}]}}]};groupedHtml=semanticView(latest.sessions[0])`);
  check(`groupedHtml.includes('Current work')&&groupedHtml.includes('owner worker-1')&&groupedHtml.includes('working')`, 'Primary work identifies current owner and state');
  check(`groupedHtml.includes('Historical work')&&groupedHtml.includes('Old result')`, 'Completed and unrelated work is disclosed as history');
  check(`['Approvals / blockers','Tests','Files','Commands','Metrics'].every(label=>groupedHtml.includes(label))`, 'Evidence is grouped into operational questions');
});

regression('Two-pane selection survives updates, fallback and filtering without resetting pane scroll', () => {
  run(`latest={sessions:[{id:'a',cwd:'C:/App',status:'working'},{id:'b',parent:'a',status:'idle'}]};selectedId='b';render()`);
  check(`selectedId==='b'&&foundryView.getSelected()==='b'`);
  run(`$('#content').scrollTop=71;$('#session-detail').scrollTop=113;latest.sessions.reverse();render()`);
  check(`selectedId==='b'&&$('#content').scrollTop===71&&$('#session-detail').scrollTop===113`);
  run(`latest={sessions:[{id:'c',status:'idle',approval_pending:true,cwd:'C:/Review'},{id:'w',status:'working',cwd:'C:/Work'}]};render()`);
  check(`selectedId==='c'`,'Removed selection falls back to approval first');
  run(`filter='working';render()`);
  check(`selectedId==='w'`,'Filtering selects a visible session');
  run(`filter='all';latest={sessions:[{id:'done',cwd:'C:/Archive',status:'completed'}]};render()`);
  check(`$('#content').innerHTML.includes('Completed session Archive')&&!$('#content').innerHTML.includes('data-state-key="session:done:root" open')`);
});
regression('Selection detail separates runtime identity and account/session usage', () => {
  run(`detailHtml=detail({id:'identity',cwd:'C:/App',status:'idle',harness:'hermes',provider:'openrouter',model:'independent-model',task:'<script>no</script>'})`);
  check(`['Harness','hermes','Provider','openrouter','Model','independent-model','Session usage','Provider account'].every(text=>detailHtml.includes(text))`);
  check(`detailHtml.includes('&lt;script&gt;')&&!detailHtml.includes('<script>')`);
  check(`limitRow({used_percent:null}).includes('Usage unavailable')&&!limitRow({used_percent:null}).includes('0% used')`,'Missing account usage must not appear as zero');
  check(`limitRow({used_percent:0,window_minutes:300}).includes('0% used')`,'A reported zero remains valid');
});
regression('Bounded history is explicitly labeled even without retained observations', () => {
  check(`semanticView({id:'bounded',trace:{truncated:true}},'work').includes('History abbreviated')`);
  check(`!semanticView({id:'complete',trace:{truncated:false}},'work').includes('History abbreviated')`);
});
if (failures.length) throw new AggregateError(failures, failures.length + ' Operations regressions failed');
console.log('Operations: canonical project/session grouping, honest summaries, operational states and current semantic work passed.');
