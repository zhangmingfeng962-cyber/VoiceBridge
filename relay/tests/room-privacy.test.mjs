import test from 'node:test';import assert from 'node:assert/strict';import vm from 'node:vm';import {readFileSync} from 'node:fs';import {webcrypto} from 'node:crypto';
const flush=async()=>{for(let i=0;i<8;i++)await new Promise(resolve=>setImmediate(resolve));};
function page(getUserMedia){
  const elements=new Map(),intervals=[];const element=()=>({value:'',textContent:'',disabled:false,hidden:true,events:{},style:{},classList:{toggle(){}},addEventListener(name,fn){this.events[name]=fn;},click(){this.events.click?.();},setAttribute(name,value){this[name]=value;},replaceChildren(){},append(){},dispatchEvent(event){this.events[event.type]?.();}});
  const get=id=>{if(!elements.has(id))elements.set(id,element());return elements.get(id);};get('voiceVolume').value='100';get('volume').value='70';let opened=0,closed=0;
  class Socket {static OPEN=1;constructor(){this.readyState=1;opened++;setImmediate(()=>this.onopen?.());}send(text){const data=JSON.parse(text);if(data.type==='join'){this.onmessage?.({data:JSON.stringify({type:'welcome',id:'c'.repeat(32)})});this.onmessage?.({data:JSON.stringify({type:'members',members:[{id:'c'.repeat(32),name:'测试',microphone:false}]})});}}close(){this.readyState=3;}}
  class Context {constructor(){this.currentTime=0;this.destination={};this.audioWorklet={addModule:async()=>{}};}async resume(){}async close(){closed++;}createGain(){return {gain:{value:1,setTargetAtTime(){}},connect(){return this;},disconnect(){}};}createMediaStreamSource(){return {connect(){},disconnect(){}};}}
  class Processor {constructor(){this.port={postMessage(){}};}connect(){return this;}disconnect(){}}
  const window={isSecureContext:true,AudioWorkletNode:Processor,addEventListener(){}};
  vm.runInNewContext(readFileSync(new URL('../web/room.js',import.meta.url),'utf8'),{document:{getElementById:get,createElement:element},location:{pathname:'/room/'+'a'.repeat(32),hash:'#'+'b'.repeat(64)+'.'+'d'.repeat(64),protocol:'https:',host:'room.example'},window,navigator:{mediaDevices:{getUserMedia}},WebSocket:Socket,AudioContext:Context,AudioWorkletNode:Processor,crypto:webcrypto,TextEncoder,Uint8Array,Float32Array,DataView,ArrayBuffer,Map,Set,JSON,Number,Math,Date,Promise,setTimeout,clearTimeout,setInterval:fn=>{intervals.push(fn);return intervals.length;},clearInterval(){},Event:class{constructor(type){this.type=type;}}});
  return {get,get opened(){return opened;},get closed(){return closed;}};
}
test('entering a voice room never requests microphone; explicit toggle releases every track',async()=>{
  let requested=0,stopped=0;const track={stop(){stopped++;}},stream={getTracks:()=>[track],getAudioTracks:()=>[track]};const p=page(async()=>{requested++;return stream;});
  assert.equal(requested,0);p.get('join').click();await flush();assert.equal(p.opened,1);assert.equal(requested,0);assert.match(p.get('voiceStatus').textContent,/麦克风关闭/);
  p.get('microphone').click();await flush();assert.equal(requested,1);assert.equal(stopped,0);assert.equal(p.get('microphone')['aria-pressed'],'true');
  p.get('microphone').click();await flush();assert.equal(stopped,1);assert.equal(p.get('microphone')['aria-pressed'],'false');p.get('leave').click();await flush();assert.equal(p.closed,1);
});
test('leaving during a pending browser permission cannot leave a newly granted microphone active',async()=>{
  let grant,stopped=0;const p=page(()=>new Promise(resolve=>{grant=resolve;}));p.get('join').click();await flush();p.get('microphone').click();await flush();p.get('leave').click();await flush();grant({getTracks:()=>[{stop(){stopped++;}}]});await flush();assert.equal(stopped,1);assert.equal(p.get('microphone')['aria-pressed'],'false');assert.equal(p.get('microphone').disabled,true);
});
test('a virtual cable is stopped before connecting to room voice or announcing an open microphone',async()=>{
  let stopped=0;const track={label:'CABLE Output (VB-Audio Virtual Cable)',stop(){stopped++;}};const p=page(async()=>({getTracks:()=>[track],getAudioTracks:()=>[track]}));p.get('join').click();await flush();p.get('microphone').click();await flush();assert.equal(stopped,1);assert.match(p.get('voiceStatus').textContent,/虚拟输入/);assert.notEqual(p.get('microphone')['aria-pressed'],'true');p.get('leave').click();await flush();
});
