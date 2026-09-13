// Run the page's real state, movement and inspector code without browser dependencies.
const fs = require('node:fs'), vm = require('node:vm'), assert = require('node:assert/strict');
new vm.Script(fs.readFileSync('index.html','utf8').split('<script>')[1].split('</script>')[0], {filename:'index.html'});
const drawing = new Proxy({}, {get:(_,key)=>key==='measureText'?text=>({width:String(text).length*6}):
  /Gradient$/.test(key)?()=>({addColorStop(){}}):()=>{}});
const elements = new Map();
function element(key){if(!elements.has(key))elements.set(key,{
  clientWidth:1100,clientHeight:800,dataset:{},style:{},listeners:{},innerHTML:'',textContent:'',
  classList:{add(){},remove(){},toggle(){}},getContext:()=>drawing,
  addEventListener(name,fn){(this.listeners[name]??=[]).push(fn)},setAttribute(){},
  append(...children){(this.children??=[]).push(...children)},prepend(...children){this.children=[...children,...(this.children||[])]},
  lastElementChild:{},getBoundingClientRect:()=>({left:0,top:0}),setPointerCapture(){},releasePointerCapture(){}
});return elements.get(key)}
const pendingFrames=new Map(),pageListeners={},documentListeners={};let frameId=0;
function pumpFrames(ms){const frames=[...pendingFrames.values()];pendingFrames.clear();for(const callback of frames)callback(ms)}
const sandbox=vm.createContext({console,Map,Set,Math,JSON,Number,String,Date,Array,Object,
  document:{readyState:'loading',hidden:false,focused:true,hasFocus(){return this.focused},querySelector:element,createElement:()=>element(Symbol()),addEventListener(name,fn){(documentListeners[name]??=[]).push(fn)}},
  window:{addEventListener(name,fn){(pageListeners[name]??=[]).push(fn)}},
  location:{pathname:'/observatory'},
  devicePixelRatio:1,matchMedia:()=>({matches:false,addEventListener(){}}),
  requestAnimationFrame(fn){pendingFrames.set(++frameId,fn);return frameId},cancelAnimationFrame(id){pendingFrames.delete(id)},
  performance:{now:()=>0},ResizeObserver:class{observe(){}},EventSource:class{addEventListener(){}},
  fixture:JSON.parse(fs.readFileSync('example-state.json','utf8'))});
vm.runInContext(fs.readFileSync('foundry.js','utf8'),sandbox);
vm.runInContext(fs.readFileSync('observatory.html','utf8').split('<script>')[1].split('</script>')[0],sandbox);
const run=expression=>vm.runInContext(expression,sandbox);
function check(expression,message=expression){assert.equal(run(expression),true,message)}
check('semanticInspector({id:"bounded",trace:{truncated:true}}).includes("History abbreviated")');
check('!semanticInspector({id:"complete",trace:{truncated:false}}).includes("History abbreviated")');

run('sync(fixture);choose("tests");camera.zoom=1.3;camera.x=42;camera.y=-18;camera.yaw=.9;camera.pitch=.7;sync(fixture)');
check('workers.size===4 && areas.length===1');
check('workers.get("nested").area.root.id==="lead"');
check('selected==="tests" && camera.zoom===1.3 && camera.x===42 && camera.y===-18 && camera.yaw===.9 && camera.pitch===.7');
check('workers.get("tests").station===areas[0].workstreams.find(s=>s.label==="Run verification suites").slot');
check('detail.innerHTML.includes("Integrate the release")', 'A selected subagent exposes its parent task in the inspector');

// New/reordered unrelated roots must not teleport an existing community or worker.
run('var oldArea={x:areas[0].x,z:areas[0].z};var oldWorker={x:workers.get("nested").x,z:workers.get("nested").z};var expanded={sessions:[{id:"another-root",cwd:"A:/Another",status:"idle"},...fixture.sessions.slice().reverse()]};sync(expanded)');
check('workers.get("nested").area.x===oldArea.x && workers.get("nested").area.z===oldArea.z');
check('workers.get("nested").x===oldWorker.x && workers.get("nested").z===oldWorker.z');
check('selected==="tests" && camera.zoom===1.3 && camera.yaw===.9 && camera.pitch===.7');

// Changing assignment moves a person along a bounded route, then stops at the desk.
run('var next=JSON.parse(JSON.stringify(expanded));next.sessions.find(a=>a.id==="nested").task="Implement project changes";var before={x:workers.get("nested").x,z:workers.get("nested").z};sync(next);var w=workers.get("nested");');
check('w.x===before.x && w.z===before.z', 'A feed refresh preserves the current travel position');
run('paused=false;workerPosition(w,1,.05)');
check('Math.hypot(w.x-before.x,w.z-before.z)>0 && Math.hypot(w.x-before.x,w.z-before.z)<.3', 'Assignment travel starts at walking speed');
run('for(var step=0;step<800;step++)workerPosition(w,step*.05,.05)');
check('Math.hypot(w.x-w.targetX,w.z-w.targetZ)<.001', 'Travel reaches the assigned station');
run('before={x:w.x,z:w.z};workerPosition(w,99,.05)');
check('w.x===before.x && w.z===before.z', 'Working at a station does not wander around it');
run('w.x+=1;before={x:w.x,z:w.z};paused=true;workerPosition(w,100,.05)');
check('w.x===before.x && w.z===before.z', 'Pausing freezes even a worker already in transit');
run('paused=false;document.hidden=true;workerPosition(w,101,.05)');
check('w.x===before.x && w.z===before.z', 'Hidden pages freeze travel');
run('document.hidden=false');

check('JSON.stringify([{status:"running"},{status:"tool"},{status:"working",current_action:"Waiting for delegated agents"},{status:"done"},{status:"failed"},{status:"idle"},{status:"idle",working_on:{status:"idle",summary:"Waiting for another turn"}},{status:"working",stale:true},{status:"idle",approval_pending:true}].map(statusFor))===JSON.stringify(["working","tool","waiting","completed","failed","idle","idle","stale","approval"])', 'Every operational state has an explicit visual meaning');

// Extra provider fields must never become inspector content; allowed text is escaped.
run('var privateState={sessions:[{id:"safe",name:"<img src=x onerror=alert(1)>",nickname:"<img src=x onerror=alert(1)>",task:"Safe task",current_action:"Safe action",status:"working",message:"SECRET_MESSAGE",reasoning_summary:"SECRET_REASONING",recent_activity:[{detail:"<script>safe text</script>",status:"completed",body:"SECRET_BODY"}]}]};sync(privateState);choose("safe")');
check('detail.innerHTML.includes("Safe action") && detail.innerHTML.includes("&lt;img") && detail.innerHTML.includes("&lt;script&gt;")');
check('!detail.innerHTML.includes("SECRET_") && !detail.innerHTML.includes("<img") && !detail.innerHTML.includes("<script>")');
run('hovered="safe";sync({sessions:[]})');
check('workers.size===0 && selected==="" && hovered===""', 'Removed workers cannot leave a stale selection or hover');
run('sync(fixture);drawScene(1000)');
check('hits.some(h=>h.id==="tests")', 'The rendered scene exposes worker hit targets');
const canvas=elements.get('#world');
assert.ok(canvas.listeners.keydown?.length, 'Canvas offers keyboard camera controls');
const oldX=run('camera.x');
for(const handler of canvas.listeners.keydown) handler({key:'ArrowRight',preventDefault(){},target:canvas});
assert.notEqual(run('camera.x'),oldX,'Arrow keys pan the camera');
const regressions=[];
function regression(name,body){try{body()}catch(error){console.error(name+': '+error.message);regressions.push(error)}}
regression('Fit includes actual districts and surviving gaps',()=>{
  run('var fitRoots=Array.from({length:8},(_,i)=>({id:"fit-"+i,status:"idle"}));sync({sessions:fitRoots})');
  for(const ids of [[0,1],[0,1,2,3],[7]]){
    sandbox.fitIds=ids;
    run('sync({sessions:fitIds.map(i=>fitRoots[i])});fitView()');
    check('areas.every(a=>[-5.7,5.7].every(x=>[-5.7,5.7].every(z=>[0,4.7].every(y=>{const p=project(a.x+x,a.z+z,y);return p.x>=24&&p.x<=canvas.clientWidth-24&&p.y>=24&&p.y<=canvas.clientHeight-24}))))','Every district fits inside the viewport');
  }
  run('workers.values().next().value.x-=20;fitView()');
  check('[...workers.values()].every(w=>{const p=project(w.x,w.z,2);return p.x>=24&&p.x<=canvas.clientWidth-24&&p.y>=24&&p.y<=canvas.clientHeight-24})','Fit includes a worker still travelling from its previous district');
});
regression('Inspector uses normalized state and approval guidance',()=>{
  run('detail.children=[];sync({sessions:[{id:"normalized",status:"working",approval_pending:true}]});choose("normalized")');
  check('detail.innerHTML.includes("status approval") && detail.innerHTML.includes(">approval</span>")','Pending approval must change the visible badge');
  check('(detail.children||[]).some(child=>child.className==="approval-note")','Pending approval must expose approval guidance');
  check('detail.innerHTML.includes("Approval required before work can continue")','An approval without a recorded action uses the generic fallback');
  run('sync({sessions:[{id:"normalized",status:"working",approval_pending:true,current_action:"Allow writing the release archive"}]});choose("normalized")');
  check('detail.innerHTML.includes("Allow writing the release archive") && !detail.innerHTML.includes("Approval required before work can continue")','Approval preserves the exact recorded action awaiting permission');
  for(const [session,expected] of [[{status:'working',current_action:'Waiting for delegated agents'},'waiting'],[{status:'running',stale:true},'stale'],[{status:'done'},'completed'],[{status:'running'},'working']]){
    sandbox.normalizedSession={id:'normalized',...session};sandbox.expectedStatus=expected;
    run('sync({sessions:[normalizedSession]});choose("normalized")');
    check('detail.innerHTML.includes("status "+expectedStatus)','Inspector badge must match the figure state: '+expected);
  }
});
regression('Assignment travel clears the solid task hub',()=>{
  run('var routeState={sessions:[{id:"route-root",status:"working",task:"Inspect release"}]};sync(routeState);var walker=workers.get("route-root");routeState.sessions[0].task="Implement release";sync(routeState);walker=workers.get("route-root");paused=false');
  check('(()=>{for(let i=0;i<1200;i++){workerPosition(walker,i*.01,.01);if(Math.abs(walker.x-walker.area.x)<1.05&&Math.abs(walker.z-(walker.area.z-.4))<1.05)return false}return Math.hypot(walker.x-walker.targetX,walker.z-walker.targetZ)<.001})()','The entire walked route must clear the hub footprint and reach its station');
});
regression('Extra paints cannot accelerate assignment travel',()=>{
  run('var moveState={sessions:[{id:"paint-root",status:"working",task:"Inspect release"}]};sync(moveState);moveState.sessions[0].task="Implement release";sync(moveState);var runner=workers.get("paint-root"),savedRun={x:runner.x,z:runner.z,route:runner.route.map(p=>({...p}))};paused=false;renderedAt=0;for(let ms=50;ms<=500;ms+=50)frame(ms);var regularPosition={x:runner.x,z:runner.z};runner.x=savedRun.x;runner.z=savedRun.z;runner.route=savedRun.route.map(p=>({...p}));renderedAt=0;for(let ms=50;ms<=500;ms+=50){for(let paintCount=0;paintCount<12;paintCount++)drawScene(ms);frame(ms)}');
  check('Math.hypot(runner.x-regularPosition.x,runner.z-regularPosition.z)<.00001','Equal elapsed time gives equal travel with or without pointer paints');
  check('Math.hypot(runner.x-savedRun.x,runner.z-savedRun.z)>0','Animation frames still advance travel');
});
regression('Animation frames reuse unchanged city geometry',()=>{
  run(`sync(fixture);var gridDraws=0,savedDrawGrid=drawGrid;drawGrid=()=>{gridDraws++;savedDrawGrid()};drawScene(1000);drawScene(1034)`);
  check('gridDraws===1','Unchanged animation frames must reuse the static city layer');
  run('drawGrid=savedDrawGrid');
});
regression('CSS viewport changes invalidate static geometry even at the same backing size',()=>{
  run(`sync(fixture);canvas.clientWidth=1100;canvas.clientHeight=800;devicePixelRatio=1;staticDirty=true;var viewportGridDraws=0,savedViewportGrid=drawGrid;drawGrid=()=>{viewportGridDraws++;savedViewportGrid()};drawScene(1000);canvas.clientWidth=550;canvas.clientHeight=400;devicePixelRatio=2;drawScene(1034)`);
  check('viewportGridDraws===2','A changed CSS viewport must rebuild the projected city');
  run('drawGrid=savedViewportGrid;canvas.clientWidth=1100;canvas.clientHeight=800;devicePixelRatio=1;staticDirty=true');
});
regression('Following a stationary worker preserves the static city cache',()=>{
  run(`sync({sessions:[{id:'still-follow',cwd:'C:/Follow',status:'working'}]});setFollow('still-follow');var followGridDraws=0,savedFollowGrid=drawGrid;drawGrid=()=>{followGridDraws++;savedFollowGrid()};drawScene(1000);focusWorker('still-follow');drawScene(1034)`);
  check('followGridDraws===1','A stationary followed worker must not invalidate unchanged city geometry');
  run('drawGrid=savedFollowGrid;clearFocus()');
});
regression('Semantic trace drives the inspector and station without exposing provider extras',()=>{
  run('sync({sessions:[{id:"semantic",status:"working",current_action:"Generic fallback",prompt_context:{summary:"Integrate semantic trace",text:"Integrate semantic trace with safe details",source:"user_prompt"},working_on:{summary:"Run semantic checks",source:"agent_commentary",status:"working",task_path:["Integrate semantic trace","Run semantic checks"]},work_breakdown:[{id:"root",kind:"prompt",summary:"Integrate semantic trace"},{id:"review",parent_id:"root",kind:"step",summary:"Run semantic checks"}],trace:{version:1,root_id:"root",observations:[{id:"root",kind:"prompt",summary:"Integrate semantic trace",detail:"Integrate semantic trace with safe details"},{id:"review",parent_id:"root",kind:"step",summary:"Run semantic checks"},{id:"test",parent_id:"review",kind:"test",summary:"node test_observatory.cjs",exit_code:0}]},reasoning_summary:"SECRET_REASONING"}]});choose("semantic")');
  check('workstreamFor(workers.get("semantic"))==="Run semantic checks"','Recorded task path supplies a stable workstream when no assignment exists');
  check('detail.innerHTML.includes("Run semantic checks") && detail.innerHTML.includes("Integrate semantic trace") && !detail.innerHTML.includes("Task path")','Semantic working context is visible once while a wholly redundant task path is omitted');
  check('detail.innerHTML.includes("<details") && !detail.innerHTML.includes("SECRET_REASONING")','Prompt and evidence details require explicit expansion and unrecognized fields stay hidden');
});
regression('Malformed semantic arrays and cycles degrade without changing explicit semantic status',()=>{
  run('sync({sessions:[{id:"guarded",status:"working",working_on:{summary:"Wait safely",status:"idle"},trace:{observations:{}},work_breakdown:[{id:"a",parent_id:"b",kind:"step",summary:"Cycle A"},{id:"b",parent_id:"a",kind:"step",summary:"Cycle B"}]}]});choose("guarded")');
  check('statusFor(workers.get("guarded"))==="idle" && detail.innerHTML.includes("Cycle A")','Malformed evidence arrays are ignored, cycles are bounded, and working_on status wins');
});
regression('Inspector semantic disclosures retain their explicit open state across sync',()=>{
  run('var promptDisclosure={open:true,dataset:{semanticKey:"guarded:prompt"}},newEvidence={open:false,dataset:{semanticKey:"guarded:evidence"}},disclosures=[promptDisclosure,newEvidence];detail.querySelectorAll=()=>disclosures;updateDetail();sync({sessions:[{id:"guarded",status:"idle",working_on:{summary:"Wait safely",status:"idle"}}]});');
  check('inspectorOpen.has("guarded:prompt") && promptDisclosure.open===true && promptDisclosure.dataset.semanticKey==="guarded:prompt" && newEvidence.open===false','Only the explicitly opened semantic disclosure survives a reordered live state');
});
regression('Root rail and worker actions stay bound through hover and state refresh',()=>{
  run('sync(fixture);choose("tests");openActions("tests");hovered="nested";updateDetail();setFollow("tests");sync(fixture)');
  check('menuTarget==="tests" && selected==="tests" && following==="tests" && detail.innerHTML.includes("Run verification suites")','Hover and SSE do not retarget the selected worker action or inspector');
  check('activeRoots().length===1 && activeRoots()[0].id==="lead" && activeRoots()[0].active_agents===2','The rail groups active agents by root session, excluding completed workers');
  run('clearFocus()');
  check('following==="" && menuTarget===""','Clearing focus exits the compact action state');
});
regression('Eligible Codex workers expose the shared confirmed send flow as the primary action',()=>{
  run(`foundryBootstrap={configuration:{adapter:'codex'},capabilities:[{adapter:'codex',selected:true,send_followup:true}]};sentSession=null;sendDialog=(session,label)=>sentSession={session,label};sync({sessions:[{id:'codex-live',cwd:'C:/Send',status:'working',harness:'codex'}]});openActions('codex-live')`);
  check(`actionMenu.innerHTML.startsWith('<button class="primary" type="button" data-action="send">Send instruction…</button>')`,'Send is the primary worker action only when the shared capability gate allows it');
  for(const handler of elements.get('#action-menu').listeners.click)handler({target:{dataset:{action:'send'}}});
  check(`sentSession.session.id==='codex-live'&&sentSession.label.includes('Send')`,'The worker action delegates to the existing send dialog with the selected destination');
  run(`sync({sessions:[{id:'hermes-live',cwd:'C:/Other',status:'working',harness:'hermes'}]});openActions('hermes-live')`);
  check(`!actionMenu.innerHTML.includes('data-action="send"')`,'Unsupported harnesses do not expose a send control');
});
regression('Project rail combines canonical roots and keeps selected session identity',()=>{
  sandbox.projectFixture={sessions:[
    {id:'root-a',cwd:'\\\\?\\C:\\Work\\Foundry',status:'working',working_on:{summary:'Coordinate audit',status:'working'}},
    {id:'child-a',parent:'root-a',status:'working',task:'Fix inspector',working_on:{summary:'Render current work',status:'working'}},
    {id:'root-b',cwd:'c:/work/foundry/',status:'completed',task:'Prior session'}]};
  run(`sync(projectFixture);choose('child-a');renderRail()`);
  check('areas.length===1 && areas[0].sessions.length===2','Canonical cwd creates one district while retaining both root sessions');
  check('rail.innerHTML.includes("2 sessions") && rail.innerHTML.includes("child-a")','Project rail lists its sessions and agents below the project');
  check('rail.innerHTML.includes("project-active")','Selecting a child highlights its parent project');
});
regression('Hover preview never replaces the selected inspector',()=>{
  run(`sync({sessions:[
    {id:'selected-agent',cwd:'C:/One',status:'working',working_on:{summary:'Selected current work',status:'working'}},
    {id:'hover-agent',cwd:'C:/Two',status:'working',working_on:{summary:'Hovered current work',status:'working'}}]});choose('selected-agent');hovered='hover-agent';updateDetail()`);
  check('detail.innerHTML.includes("Selected current work") && !detail.innerHTML.includes("Hovered current work")','Hover affects the canvas only; inspector and actions remain selected');
});
regression('Follow respects frame throttling and hidden or paused state',()=>{
  run(`sync({sessions:[{id:'followed',cwd:'C:/Follow',status:'working'}]});setFollow('followed');paused=false;document.hidden=false;paint();renderedAt=100;var draws=0,savedDrawScene=drawScene;drawScene=()=>{draws++}`);
  pumpFrames(110);pumpFrames(120);
  check('draws===0','Following does not bypass the 30fps frame throttle');
  run('document.hidden=true');pumpFrames(200);run('document.hidden=false;paused=true');pumpFrames(240);
  check('draws===0','Hidden and paused follow mode does not repaint continuously');
  run('paused=false;updateAnimation()');pumpFrames(280);run('drawScene=savedDrawScene');
  check('draws===1','A due visible frame paints once');
});
regression('Idle, hidden, unfocused and paused scenes stop scheduling animation',()=>{
  run(`sync({sessions:[{id:'quiet',cwd:'C:/Quiet',status:'idle'}]});paused=false;document.hidden=false;document.focused=true;var quietDraws=0,quietScene=drawScene;drawScene=()=>quietDraws++`);
  try {
  pumpFrames(1000);
  assert.equal(run('quietDraws'),0,'An all-idle city does not repaint continuously');
  assert.equal(pendingFrames.size,0,'An all-idle city leaves no pending animation callback');
  run(`sync({sessions:[{id:'quiet',cwd:'C:/Quiet',status:'working'}]});paint();paint()`);
  assert.equal(pendingFrames.size,1,'Repeated feed paints keep exactly one animation loop');
  pumpFrames(1100);
  assert.equal(pendingFrames.size,1,'Current work continues animating');
  for(const change of ['paused=true','paused=false;document.hidden=true','document.hidden=false;document.focused=false']){
    run(change+';paint()');pumpFrames(1200);
    assert.equal(pendingFrames.size,0,'Inactive scene leaves no pending animation callback');
  }
  const before=run('quietDraws');
  run('paint();paint()');
  assert.equal(run('quietDraws'),before,'Unfocused feed events do not repaint the canvas');
  run('document.focused=true');
  for(const callback of pageListeners.focus||[])callback();
  assert.equal(pendingFrames.size,1,'Focus resumes the one loop');
  assert.equal(run('quietDraws'),before+1,'Focus paints the latest retained state');
  } finally {run('drawScene=quietScene;document.focused=true;document.hidden=false;paused=false')}
});
regression('Buildings represent stable assignments and completed workers remain history only',()=>{
  sandbox.streamFixture={sessions:[
    {id:'stream-root',cwd:'C:/Streams',status:'working',task:'Coordinate project',current_action:'Reading files'},
    {id:'builder',parent:'stream-root',status:'working',task:'Build operations',current_action:'Inspect markup'},
    {id:'reviewer',parent:'stream-root',status:'working',task:'Review operations',current_action:'Run tests'},
    {id:'done-worker',parent:'stream-root',status:'completed',task:'Old work'}]};
  run('sync(streamFixture);var streamKeys=areas[0].workstreams.map(s=>s.key),builderStation=workers.get("builder").station;streamFixture.sessions[1].current_action="Write code";sync(streamFixture);renderRail()');
  check('JSON.stringify(areas[0].workstreams.map(s=>s.key))===JSON.stringify(streamKeys)&&workers.get("builder").station===builderStation','Current action changes do not move an assignment to a different building');
  check('areas[0].workstreams.some(s=>s.label==="Build operations")&&areas[0].workstreams.some(s=>s.label==="Review operations")','Buildings carry recorded assignment labels');
  check('cityWorkers().every(w=>w.id!=="done-worker")&&picker.innerHTML.includes("done-worker")&&rail.innerHTML.includes("done-worker")','Completed workers leave the active city but remain selectable in project history');
  run('sync({sessions:streamFixture.sessions.slice().reverse()})');
  check('areas.length===1&&areas[0].sessions.length===1&&areas[0].key===workers.get("builder").area.key','Reordered SSE preserves the canonical project district and root relationship');
});
regression('Observatory inspector bounds history and groups evidence',()=>{
  run(`sync({sessions:[{id:'inspector',cwd:'C:/Inspect',status:'working',working_on:{summary:'Run checks',observation_id:'now',status:'working'},work_breakdown:[{id:'root-work',kind:'prompt',summary:'Audit UI',session_id:'inspector',status:'working'},{id:'now',parent_id:'root-work',kind:'step',summary:'Run checks',session_id:'worker-1',status:'working'},{id:'child',parent_id:'now',kind:'step',summary:'Review results',session_id:'worker-3',status:'waiting'},{id:'old',parent_id:'root-work',kind:'outcome',summary:'Old result',session_id:'worker-2',status:'completed'}],trace:{observations:[{id:'approval',kind:'approval',summary:'Needs permission'},{id:'test',kind:'test',summary:'node tests',exit_code:0},{id:'file',kind:'file',summary:'observatory.html'},{id:'tool',kind:'tool',tool:'node'},{id:'metric',kind:'metric',summary:'33ms'}]}}]});choose('inspector')`);
  check('detail.innerHTML.includes("Work breakdown")&&detail.innerHTML.includes("owner worker-3")&&detail.innerHTML.includes("waiting")&&detail.innerHTML.includes("Historical work")','Inspector separates distinct owner/state context from historical work');
  check('["Approvals / blockers","Tests","Files","Commands","Metrics"].every(label=>detail.innerHTML.includes(label))','Inspector groups evidence by operator question');
});
regression('Terminal turn state folds history and projects sort by attention',()=>{
  check(`JSON.stringify([
    {status:'idle',turn_status:'completed'},
    {status:'idle',turn_status:'failed'},
    {status:'working',turn_status:'completed'},
    {status:'idle',turn_status:'completed',working_on:{status:'working'}},
    {status:'idle',turn_status:'completed',approval_pending:true},
    {status:'idle',current_action:'Waiting for another turn'}].map(statusFor))===JSON.stringify(['completed','failed','working','working','approval','idle'])`, 'Generic terminal turn state wins unless explicit current work or approval supersedes it');
  run(`sync({sessions:[
    {id:'idle-project',cwd:'C:/Zeta',status:'idle'},
    {id:'active-project',cwd:'C:/Active',status:'working'},
    {id:'approval-project',cwd:'C:/Review',status:'idle',approval_pending:true},
    {id:'done-project',cwd:'C:/Archive',status:'idle',turn_status:'completed'}]});renderRail();orderedCityProjects=activeRoots()`);
  check(`JSON.stringify(orderedCityProjects.map(group=>projectName(group.cwd)))===JSON.stringify(['Review','Active','Archive','Zeta'])`, 'Observatory rail prioritizes approval, active, then inactive projects');
  check(`rail.innerHTML.includes('History (1)')&&cityWorkers().every(worker=>worker.id!=='done-project')`, 'Terminal root remains selectable in folded history and leaves the active city');
});
regression('Inspector states current work once and shows only distinct context',()=>{
  sandbox.repeatedSummary='Implement the deliberately long current summary without repeating it anywhere in the inspector';
  run(`sync({sessions:[{id:'dedupe',cwd:'C:/Dedupe',status:'working',task:'Audit interface',prompt_context:{summary:'Audit interface'},working_on:{summary:repeatedSummary,observation_id:'now',status:'working',task_path:['Audit interface',repeatedSummary]},work_breakdown:[{id:'prompt',kind:'prompt',summary:'Audit interface'},{id:'now',parent_id:'prompt',kind:'step',summary:repeatedSummary,session_id:'dedupe',status:'working'}]}]});choose('dedupe')`);
  check(`detail.innerHTML.split(repeatedSummary).length===2`, 'Exact current summary appears once');
  check(`!detail.innerHTML.includes('Task path')&&!detail.innerHTML.includes('Current work')`, 'Redundant task path and current-work panels are omitted');
  run(`var dedupe=state.sessions[0];dedupe.working_on.task_path=['Audit interface','Release train',repeatedSummary];dedupe.work_breakdown.push({id:'child-check',parent_id:'now',kind:'step',summary:'Run browser check',session_id:'reviewer',status:'working'});sync({sessions:[dedupe]});choose('dedupe')`);
  check(`detail.innerHTML.includes('Release train')&&detail.innerHTML.includes('Work breakdown')&&detail.innerHTML.includes('Run browser check')&&!semanticInspector(workers.get('dedupe')).includes(repeatedSummary)`, 'Distinct path context and child work remain visible without restating current work');
});
regression('Completed-only districts stay in history but not scene or Fit bounds',()=>{
  sandbox.sceneHistory={sessions:[...Array.from({length:7},(_,i)=>({id:'done-'+i,cwd:'C:/Done-'+i,status:'idle',turn_status:'completed'})),{id:'live-project',cwd:'C:/Live',status:'working',task:'Current work'}]};
  run(`sync(sceneHistory);var allDistricts=areas.length,visibleDistricts=activeAreas().length;fitView();var historyZoom=camera.zoom;var savedDrawArea=drawArea,drawn=[];drawArea=area=>drawn.push(area.key);drawScene(1000);drawArea=savedDrawArea`);
  check(`allDistricts===8&&visibleDistricts===1&&drawn.length===1&&drawn[0]===workers.get('live-project').area.key`, 'Only the nonterminal district is drawn');
  run(`sync({sessions:[sceneHistory.sessions.at(-1)]});fitView();var liveOnlyZoom=camera.zoom`);
  check(`Math.abs(historyZoom-liveOnlyZoom)<.000001`, 'Completed-only districts do not reduce Fit scale');
});
if(regressions.length)throw new AggregateError(regressions,regressions.length+' observatory regressions failed');
const observatorySource=fs.readFileSync('observatory.html','utf8');
assert(observatorySource.includes('grid-template-columns:252px minmax(0,1fr) 340px'),'Dock rail and inspector around the canvas cell');
assert(!observatorySource.includes('.detail{position:absolute'),'Inspector must not float over the world');
run(`sync({sessions:[{id:'dock-root',cwd:'C:/Dock',status:'working',harness:'codex',provider:'openai',model:'separate'}]});choose('dock-root');rail.scrollTop=79;detail.scrollTop=115;rail.querySelectorAll=()=>[{dataset:{railKey:'project:win:c:/dock'},open:false}];renderRail();updateDetail()`);
check(`rail.scrollTop===79&&detail.scrollTop===115`,'Live events preserve independent pane scroll');
check(`railOpen.get('project:win:c:/dock')===false&&rail.innerHTML.includes('data-rail-key="project:win:c:/dock"')`,'Project fold state uses stable project identity');
check(`['Harness','codex','Provider','openai','Model','separate'].every(text=>detail.innerHTML.includes(text))`,'Inspector keeps runtime, inference and model separate');
console.log('Observatory: stable districts/camera/selection, semantic inspector, active root rail, worker actions, bounded fitting, hub-safe assignment travel, paint-independent motion, privacy, rendering and keyboard controls passed.');
