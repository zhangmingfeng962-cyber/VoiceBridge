import test from 'node:test';
import assert from 'node:assert/strict';
import {once} from 'node:events';
import {createHash} from 'node:crypto';
import WebSocket from 'ws';
import {startServer} from './test-server.mjs';
import {Outbound} from '../outbound.mjs';
const master='1'.repeat(64),publishKeyHash=createHash('sha256').update(master).digest('hex');
function inbox(socket){const items=[],waiting=[];socket.on('message',(data,binary)=>{const value=binary?data:JSON.parse(data),index=waiting.findIndex(x=>x.match(value));if(index>=0)waiting.splice(index,1)[0].done(value);else items.push(value);});return match=>{const index=items.findIndex(match);if(index>=0)return Promise.resolve(items.splice(index,1)[0]);return new Promise((resolve,reject)=>{const item={match,done:value=>{clearTimeout(timer);resolve(value);}},timer=setTimeout(()=>{waiting.splice(waiting.indexOf(item),1);reject(Error('Message timeout'));},2500);waiting.push(item);});};}
async function setup(){let clock=100000;const service=await startServer({host:'127.0.0.1',port:0,publishKeyHash,now:()=>clock});const clients=[];return {service,clients,tick:ms=>clock+=ms,close:async()=>{clients.forEach(x=>x.terminate());await service.stop();}};}
async function room(ctx){return (await fetch(ctx.service.url+'/api/rooms',{method:'POST',headers:{Authorization:'Bearer '+master},body:'{}'})).json();}
async function connection(ctx,r,input={}){const socket=new WebSocket(ctx.service.url.replace('http:','ws:')+'/api/rooms/'+r.id+'/voice',{origin:ctx.service.url}),next=inbox(socket);ctx.clients.push(socket);await once(socket,'open');socket.send(JSON.stringify({type:'join',version:3,role:'browser',token:r.listenToken,name:'测试成员',canResume:true,...input}));return {socket,next,...await next(m=>m.type==='welcome')};}
async function denied(ctx,r,input){const socket=new WebSocket(ctx.service.url.replace('http:','ws:')+'/api/rooms/'+r.id+'/voice',{origin:ctx.service.url});ctx.clients.push(socket);await once(socket,'open');const closed=once(socket,'close');socket.send(JSON.stringify({type:'join',version:3,role:'browser',token:r.listenToken,canResume:true,...input}));return (await closed)[0];}
test('active rooms survive three hours, host exit, and empty rooms expire only after thirty minutes',async()=>{
 const ctx=await setup();try{const r=await room(ctx),a=await connection(ctx,r,{hostToken:r.writeToken}),b=await connection(ctx,r);ctx.tick(10800001);ctx.service.maintenance({heartbeat:false});assert.ok(ctx.service.rooms.has(r.id));
 a.socket.send(JSON.stringify({type:'leave'}));await b.next(m=>m.type==='members'&&m.members.length===1);assert.equal(b.socket.readyState,1);assert.equal((await fetch(ctx.service.url+'/api/rooms/'+r.id,{method:'DELETE',headers:{Authorization:'Bearer '+r.writeToken}})).status,409);
 const closed=once(b.socket,'close');b.socket.send(JSON.stringify({type:'leave'}));await closed;ctx.tick(1799999);ctx.service.maintenance({heartbeat:false});assert.ok(ctx.service.rooms.has(r.id));ctx.tick(1);ctx.service.maintenance({heartbeat:false});assert.equal(ctx.service.rooms.has(r.id),false);
 }finally{await ctx.close();}
});
test('transient disconnect resumes the same identity and capability without losing anti replay state',async()=>{
 const ctx=await setup();try{const r=await room(ctx),a=await connection(ctx,r,{role:'desktop'}),b=await connection(ctx,r);a.socket.send(JSON.stringify({type:'flags',microphone:false,music:true}));const packet=Buffer.alloc(64);packet.write('VBA2');packet[4]=1;packet.writeUInt32LE(7,5);a.socket.send(packet);await b.next(Buffer.isBuffer);
 a.socket.terminate();const roster=await b.next(m=>m.type==='members'&&m.members.some(p=>p.id===a.id&&p.connected===false));assert.equal(roster.members.length,2);assert.equal(JSON.stringify(roster).includes(a.resumeToken),false);
 ctx.tick(60000);const restored=await connection(ctx,r,{role:'desktop',resume:{memberId:a.id,token:a.resumeToken}});assert.equal(restored.id,a.id);assert.equal(restored.controlToken,a.controlToken);assert.equal(restored.resumed,true);assert.equal(ctx.service.peers.get(a.id).lastSequences[1],7);
 restored.socket.send(JSON.stringify({type:'flags',microphone:false,music:true}));const closed=once(restored.socket,'close');restored.socket.send(packet);assert.equal((await closed)[0],4003);
 }finally{await ctx.close();}
});
test('a full room reserves a reconnecting seat, rejects stolen credentials, and releases expired seats',async()=>{
 const ctx=await setup();try{const r=await room(ctx),a=await connection(ctx,r),others=[];for(let i=0;i<7;i++)others.push(await connection(ctx,r));a.socket.terminate();await others[0].next(m=>m.type==='members'&&m.members.some(p=>p.id===a.id&&!p.connected));
 assert.equal(await denied(ctx,r,{resume:{memberId:a.id,token:r.listenToken}}),4010);assert.equal(await denied(ctx,r,{}),4009);
 const restored=await connection(ctx,r,{resume:{memberId:a.id,token:a.resumeToken}});assert.equal(restored.id,a.id);restored.socket.terminate();await others[0].next(m=>m.type==='members'&&m.members.some(p=>p.id===a.id&&!p.connected));ctx.tick(90001);ctx.service.maintenance({heartbeat:false});assert.equal(ctx.service.peers.has(a.id),false);
 assert.equal(await denied(ctx,r,{resume:{memberId:a.id,token:a.resumeToken}}),4010);const newcomer=await connection(ctx,r);assert.notEqual(newcomer.id,a.id);
 }finally{await ctx.close();}
});
test('heartbeat tolerates a delayed pong for forty four seconds before recovery is needed',async()=>{
 const ctx=await setup();try{const r=await room(ctx),a=await connection(ctx,r);ctx.tick(44000);ctx.service.maintenance();assert.equal(a.socket.readyState,1);await new Promise(resolve=>setImmediate(resolve));ctx.tick(1);assert.equal(ctx.service.peers.size,1);
 }finally{await ctx.close();}
});
test('network backpressure drops stale audio, prioritizes voice, and coalesces status without a 64KB kick',()=>{
 let now=0;const sent=[],socket={readyState:1,bufferedAmount:70000,send:(data,options,callback)=>{sent.push({data,options});callback?.();},close:(code)=>{throw Error('Unexpected close '+code);}},queue=new Outbound(socket,()=>now);
 const music=Buffer.alloc(80),voice=Buffer.alloc(80);music[20]=1;voice[20]=0;
 for(let i=0;i<20;i++)queue.push(music,true);queue.push(voice,true);queue.push({type:'members',members:[1]});queue.push({type:'members',members:[2]});assert.equal(queue.music.length,6);assert.equal(queue.controls.length,1);
 now=100;socket.bufferedAmount=0;queue.flush();assert.equal(JSON.parse(sent[0].data).members[0],2);assert.equal(sent[1].data[20],0);
 socket.bufferedAmount=70000;queue.push(voice,true);now=250;socket.bufferedAmount=0;const before=sent.length;queue.flush();assert.equal(sent.length,before);assert.ok(queue.dropped>=15);
});
