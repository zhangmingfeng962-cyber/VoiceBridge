import test from 'node:test';import assert from 'node:assert/strict';import vm from 'node:vm';import fs from 'node:fs';
const classes={};class Processor{constructor(){this.sent=[];this.port={postMessage:m=>this.sent.push(m)};}}
vm.runInNewContext(fs.readFileSync(new URL('../web/room-worklet.js',import.meta.url),'utf8'),{AudioWorkletProcessor:Processor,Float32Array,Math,Number,Map,registerProcessor:(name,type)=>classes[name]=type});
test('stereo worklet, individual gains, total mute and bounded adaptive buffering',()=>{
 const mix=new classes['voicebridge-room-mixer']();const message=m=>mix.port.onmessage({data:m});
 const packet=(key,arrival,left=.2,right=.1)=>{const samples=new Float32Array(1920);for(let n=0;n<960;n++){samples[n*2]=left;samples[n*2+1]=right;}message({type:'packet',key,arrival,samples:samples.buffer,gain:1,peak:.2});};
 for(let n=0;n<5;n++)packet('a:1',100+n*20);const output=()=>{const a=new Float32Array(128),b=new Float32Array(128);mix.process([],[ [a,b] ]);return [a,b];};
 let [a,b]=output();assert.ok(a[10]>.19&&b[10]>.09&&a[10]>b[10]*1.9);
 message({type:'gain',key:'a:1',value:0});[a,b]=output();assert.equal(a[10],0);
 for(let n=0;n<5;n++)packet('b:0',100+n*20,.05,.05);[a,b]=output();assert.ok(a[10]>.04);
 message({type:'master',value:0});[a,b]=output();assert.equal(a[10],0);
 packet('a:1',900);assert.ok(mix.streams.get('a:1').target>100);
 for(let n=0;n<1000;n++)packet('a:1',920+n*20);const state=mix.streams.get('a:1');assert.ok(state.write-state.read<=38400);assert.ok(state.trims>0);assert.ok(state.target<=400);
 message({type:'remove',id:'a'});assert.equal(mix.streams.has('a:1'),false);
});
test('capture gates reset partial frames and emit silence locally',()=>{
 const capture=new classes['voicebridge-room-capture']({processorOptions:{channels:1}});const input=[new Float32Array(128).fill(.1)],output=[new Float32Array(128).fill(1)];
 capture.port.onmessage({data:{enabled:true,gain:4,epoch:1}});for(let n=0;n<7;n++)capture.process([input],[output]);assert.equal(capture.sent.length,0);
 capture.port.onmessage({data:{enabled:false,gain:4,epoch:2}});capture.process([input],[output]);assert.equal(capture.sent.length,0);assert.equal(output[0][0],0);
 capture.port.onmessage({data:{enabled:true,gain:4,epoch:3}});for(let n=0;n<8;n++)capture.process([input],[output]);assert.equal(capture.sent.length,1);assert.equal(capture.sent[0].epoch,3);assert.ok(new Float32Array(capture.sent[0].pcm)[0]>.39);
});
test('leaving while codec support is pending closes permission stream',async()=>{
 const {BrowserAudio}=await import('../web/audio.js');let release,stopped=0;globalThis.AudioEncoder={isConfigSupported:()=>new Promise(resolve=>release=resolve)};
 const audio=new BrowserAudio(()=>{},()=>{},()=>{});audio.context={resume:async()=>{},close:async()=>{}};audio.micEpoch=7;
 const stream={getTracks:()=>[{stop:()=>stopped++}]};const task=audio.addCapture(0,stream,7);await audio.stop();release({supported:true});await task;assert.equal(stopped,1);assert.equal(audio.capture[0],null);
});
test('PTT key release invalidates queued encoded voice',async()=>{
 const {BrowserAudio}=await import('../web/audio.js');const audio=new BrowserAudio(()=>{},()=>{},()=>{});audio.options={...audio.options,microphone:true,talkMode:'ptt',talkKey:86};
 assert.equal(audio.gate(),false);audio.keyDown(86);const sending=audio.micEpoch;assert.equal(audio.gate(),true);audio.keyUp();assert.equal(audio.gate(),false);assert.ok(audio.micEpoch>sending);
});
test('member join/leave sounds follow identity changes, never nickname or microphone updates',async()=>{
 const {membershipCues}=await import('../web/room-events.js');
 assert.deepEqual(membershipCues([],[{id:'a'}],false),[true]);assert.deepEqual(membershipCues([{id:'a',name:'old'}],[{id:'a',name:'new',microphone:true}],true),[]);
 assert.deepEqual(membershipCues([{id:'a'}],[{id:'a'},{id:'b'}],true),[true]);assert.deepEqual(membershipCues([{id:'a'},{id:'b'}],[{id:'a'}],true),[false]);
});
