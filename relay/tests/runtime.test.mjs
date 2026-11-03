import test from 'node:test';import assert from 'node:assert/strict';import {Worker,MessageChannel} from 'node:worker_threads';import {once} from 'node:events';import {createHash} from 'node:crypto';import WebSocket from 'ws';import {startServer} from './test-server.mjs';import {RoomCrypto} from '../web/crypto.js';
const hash=createHash('sha256').update('1'.repeat(64)).digest('hex'),key='a'.repeat(64);
const delay=ms=>new Promise(resolve=>setTimeout(resolve,ms));
async function setup(automaticJoin=false){
 const service=await startServer({port:0,host:'127.0.0.1',publishKeyHash:hash});const room=await(await fetch(service.url+'/api/rooms',{method:'POST',headers:{Authorization:'Bearer '+'1'.repeat(64)},body:'{}'})).json();
 const worker=new Worker(new URL('./runtime-host.mjs',import.meta.url),{workerData:{origin:service.url}}),messages=[];worker.on('message',m=>messages.push(m));
 async function next(predicate,timeout=3000){const until=Date.now()+timeout;while(Date.now()<until){const index=messages.findIndex(predicate);if(index>=0)return messages.splice(index,1)[0];await delay(10);}throw Error('Runtime message timeout');}
 await next(m=>m.type==='hostReady');worker.postMessage({type:'connect',url:service.url.replace('http:','ws:')+'/api/rooms/'+room.id+'/voice',roomId:room.id,key,role:'browser',...(automaticJoin?{join:{type:'join',version:4,role:'browser',token:room.listenToken,name:'runtime',canResume:true}}:{})});await next(m=>m.type==='open');if(!automaticJoin)worker.postMessage({type:'send',data:JSON.stringify({type:'join',version:4,role:'browser',token:room.listenToken,name:'runtime',canResume:true})});const welcome=JSON.parse((await next(m=>m.type==='message'&&JSON.parse(m.data).type==='welcome')).data);
 const close=async()=>{await worker.terminate();await service.stop();};return {service,room,worker,next,welcome,close};
}
test('runtime worker restores identity and desired flags without any UI reconnect command',async()=>{
 const f=await setup();try{f.worker.postMessage({type:'desired',value:{microphone:true,sharing:true,talkMode:'free',pressed:false,micEpoch:1,shareEpoch:1}});f.worker.postMessage({type:'send',data:JSON.stringify({type:'flags',microphone:true,music:true})});await delay(30);f.service.peers.get(f.welcome.id).socket.terminate();await f.next(m=>m.type==='reconnecting');const restored=JSON.parse((await f.next(m=>m.type==='message'&&JSON.parse(m.data).type==='welcome',6000)).data);assert.equal(restored.resumed,true);assert.equal(restored.id,f.welcome.id);await delay(50);assert.equal(f.service.peers.get(restored.id).microphone,true);assert.equal(f.service.peers.get(restored.id).music,true);}finally{await f.close();}
});
test('PCM passes worker encoding/encryption; mute invalidates old epochs before asynchronous output',async()=>{
 const f=await setup();let listener;try{
  listener=new WebSocket(f.service.url.replace('http:','ws:')+'/api/rooms/'+f.room.id+'/voice',{origin:f.service.url});const received=[];listener.on('message',(bytes,binary)=>{if(binary)received.push(bytes.buffer.slice(bytes.byteOffset,bytes.byteOffset+bytes.byteLength));});await once(listener,'open');listener.send(JSON.stringify({type:'join',version:4,role:'browser',token:f.room.listenToken,name:'listener'}));await delay(40);
  const channel=new MessageChannel();f.worker.postMessage({type:'capture',kind:0,port:channel.port1},[channel.port1]);f.worker.postMessage({type:'desired',value:{microphone:true,talkMode:'free',micEpoch:1}});f.worker.postMessage({type:'send',data:JSON.stringify({type:'flags',microphone:true,music:false})});await delay(30);
  channel.port2.postMessage({pcm:new Float32Array(960).buffer,epoch:1});await delay(150);assert.equal(received.length,1);const decoded=await new RoomCrypto(f.room.id,key).openAudio(received[0]);assert.deepEqual([...new Uint8Array(decoded.opus)],[1,2,3,4,5,6,7,8]);
  f.worker.postMessage({type:'desired',value:{microphone:false,micEpoch:2}});channel.port2.postMessage({pcm:new Float32Array(960).buffer,epoch:1});channel.port2.postMessage({pcm:new Float32Array(960).buffer,epoch:2});await delay(150);assert.equal(received.length,1);channel.port2.close();
 }finally{listener?.terminate();await f.close();}
});
test('server normal close is recoverable, explicit leave does not reconnect',async()=>{
 const f=await setup();try{f.service.peers.get(f.welcome.id).socket.close(1000,'Proxy closing');const restored=JSON.parse((await f.next(m=>m.type==='message'&&JSON.parse(m.data).type==='welcome',6000)).data);assert.equal(restored.id,f.welcome.id);f.worker.postMessage({type:'send',data:JSON.stringify({type:'leave'})});await delay(1000);assert.equal(f.service.peers.size,0);}finally{await f.close();}
});
test('application pong detects one-way failure even when WebSocket protocol pongs remain alive',async()=>{
 const f=await setup();try{f.service.peers.get(f.welcome.id).socket.removeAllListeners('message');await f.next(m=>m.type==='reconnecting',26000);const restored=JSON.parse((await f.next(m=>m.type==='message'&&JSON.parse(m.data).type==='welcome',6000)).data);assert.equal(restored.id,f.welcome.id);}finally{await f.close();}
});

test('initial room join is completed by worker without waiting for a UI open callback',async()=>{const f=await setup(true);try{assert.ok(f.welcome.id);assert.equal(f.service.peers.size,1);}finally{await f.close();}});
