import test from 'node:test';
import assert from 'node:assert/strict';
import {once} from 'node:events';
import {createHash,createHmac} from 'node:crypto';
import WebSocket from 'ws';
import {startServer} from './test-server.mjs';
import {screenConfiguration,screenIce} from '../screen-room.mjs';

const master='1'.repeat(64),hash=createHash('sha256').update(master).digest('hex');
const offer={type:'offer',sdp:'v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\n'};
function mailbox(socket){
  const saved=[],pending=[];
  socket.on('message',(data,binary)=>{const value=binary?data:JSON.parse(data);const index=pending.findIndex(p=>p.match(value));if(index<0)saved.push(value);else pending.splice(index,1)[0].resolve(value);});
  return (match,fresh=false)=>{if(fresh){for(let n=saved.length-1;n>=0;n--)if(match(saved[n]))saved.splice(n,1);}const index=saved.findIndex(match);if(index>=0)return Promise.resolve(saved.splice(index,1)[0]);return new Promise((resolve,reject)=>{const item={match,resolve:value=>{clearTimeout(timer);resolve(value);}},timer=setTimeout(()=>{pending.splice(pending.indexOf(item),1);reject(Error('Screen message timeout'));},2500);pending.push(item);});};
}
async function setup(){const service=await startServer({port:0,host:'127.0.0.1',publishKeyHash:hash,screenConfig:screenConfiguration({})}),clients=[];return {service,clients,close:async()=>{clients.forEach(s=>s.terminate());await service.stop();}};}
async function room(service){return (await fetch(service.url+'/api/rooms',{method:'POST',headers:{Authorization:'Bearer '+master},body:'{}'})).json();}
async function join(ctx,r,role='browser',extra={}){
  const socket=new WebSocket(ctx.service.url.replace('http:','ws:')+`/api/rooms/${r.id}/voice`,{origin:ctx.service.url}),next=mailbox(socket);ctx.clients.push(socket);await once(socket,'open');
  socket.send(JSON.stringify({type:'join',version:3,role,token:r.listenToken,name:role,...extra}));const welcome=await next(m=>m.type==='welcome');return {socket,next,...welcome,send:message=>socket.send(JSON.stringify(message))};
}
async function claim(client,requestId='capture'){
  client.send({type:'screen:claim',requestId,mode:'balanced',surface:'window'});return (await client.next(m=>m.type==='screen:claimed'&&m.requestId===requestId)).session;
}

test('screen claim is exclusive, pause preserves audio, stop lets a different member present',async()=>{
  const ctx=await setup();try{
    const r=await room(ctx.service),a=await join(ctx,r),b=await join(ctx,r),session=await claim(a);
    b.send({type:'screen:claim',requestId:'second',mode:'balanced',surface:'monitor'});assert.equal((await b.next(m=>m.type==='screen:error')).code,'busy');
    b.send({type:'screen:update',session,paused:true,mode:'clear',surface:'monitor'});assert.equal((await b.next(m=>m.type==='screen:error')).code,'forbidden');
    a.send({type:'flags',microphone:true,music:true});await b.next(m=>m.type==='members'&&m.members.some(p=>p.id===a.id&&p.music));
    a.send({type:'screen:update',session,paused:true,mode:'clear',surface:'monitor'});const paused=await b.next(m=>m.type==='screen:state'&&m.paused);assert.equal(paused.owner,a.id);assert.equal(ctx.service.peers.get(a.id).microphone,true);assert.equal(ctx.service.peers.get(a.id).music,true);
    b.send({type:'screen:stop',session});assert.equal((await b.next(m=>m.type==='screen:error')).code,'forbidden');
    a.send({type:'screen:stop',session});await b.next(m=>m.type==='screen:state'&&!m.owner);
    assert.notEqual(await claim(b),session);
  }finally{await ctx.close();}
});

test('screen SDP and ICE are scoped to a current presenter/viewer pair, room and link epoch',async()=>{
  const ctx=await setup();try{
    const r=await room(ctx.service),r2=await room(ctx.service),a=await join(ctx,r),b=await join(ctx,r),c=await join(ctx,r2),session=await claim(a);
    let leaks=0;c.socket.on('message',(data,binary)=>{if(!binary&&JSON.parse(data).type==='screen:signal')leaks++;});
    b.send({type:'screen:watch',session});const watching=await b.next(m=>m.type==='screen:watching'),link=watching.link;await a.next(m=>m.type==='screen:viewer'&&m.joined);
    a.send({type:'screen:signal',session,link,to:c.id,description:offer});assert.equal((await a.next(m=>m.type==='screen:error')).code,'forbidden');
    a.send({type:'screen:signal',session,link,to:b.id,description:offer,from:c.id});const forwarded=await b.next(m=>m.type==='screen:signal');assert.equal(forwarded.from,a.id);assert.deepEqual(forwarded.description,offer);assert.equal(leaks,0);
    b.send({type:'screen:signal',session,link,to:a.id,description:offer});assert.equal((await b.next(m=>m.type==='screen:error')).code,'invalid');
    b.send({type:'screen:signal',session,link,to:a.id,candidates:[{candidate:'candidate:1',sdpMid:'0',sdpMLineIndex:0}]});assert.equal((await a.next(m=>m.type==='screen:signal')).candidates.length,1);
    b.send({type:'screen:watch',session,restart:true});const next=await b.next(m=>m.type==='screen:watching');assert.notEqual(next.link,link);
    a.send({type:'screen:signal',session,link,to:b.id,description:offer});assert.equal((await a.next(m=>m.type==='screen:error')).code,'forbidden');
    a.send({type:'screen:signal',session,link:next.link,to:b.id,description:{type:'offer',sdp:offer.sdp+'m=audio 9 UDP/TLS/RTP/SAVPF 111\r\n'}});assert.equal((await a.next(m=>m.type==='screen:error')).code,'invalid');
    b.send({type:'screen:unwatch',session});await a.next(m=>m.type==='screen:viewer'&&!m.joined);assert.equal(ctx.service.screens.shares.get(r.id).viewers.size,0);
  }finally{await ctx.close();}
});

test('current desktop control page owns screen capability; replacement/disconnect releases the screen without leaking to native audio',async()=>{
  const ctx=await setup();try{
    const r=await room(ctx.service),desktop=await join(ctx,r,'desktop'),viewer=await join(ctx,r);let nativeSignals=0;
    desktop.socket.on('message',(data,binary)=>{if(!binary&&JSON.parse(data).type.startsWith('screen:'))nativeSignals++;});
    const control=await join(ctx,r,'controller',{memberId:desktop.id,controlToken:desktop.controlToken});await claim(control);assert.equal(ctx.service.screens.state(r.id).owner,desktop.id);
    const replacement=await join(ctx,r,'controller',{memberId:desktop.id,controlToken:desktop.controlToken});await replacement.next(m=>m.type==='screen:state'&&!m.owner);assert.equal(ctx.service.screens.shares.has(r.id),false);
    const session=await claim(replacement),released=viewer.next(m=>m.type==='screen:state'&&!m.owner,true);replacement.socket.close();await released;assert.equal(ctx.service.screens.shares.has(r.id),false);assert.equal(nativeSignals,0);assert.ok(session);
  }finally{await ctx.close();}
});

test('ICE messages use a separate rate budget and never enter the audio packet path',async()=>{
  const ctx=await setup();try{
    const r=await room(ctx.service),a=await join(ctx,r),b=await join(ctx,r),session=await claim(a);b.send({type:'screen:watch',session});const {link}=await b.next(m=>m.type==='screen:watching');
    for(let n=0;n<70;n++)a.send({type:'screen:signal',session,link,to:b.id,candidates:[{candidate:'candidate:'+n,sdpMid:'0',sdpMLineIndex:0}]});
    a.send({type:'flags',microphone:false,music:true});const packet=Buffer.alloc(64);packet.write('VBA2');packet[4]=1;a.socket.send(packet);assert.equal((await b.next(Buffer.isBuffer)).length,80);assert.equal(a.socket.readyState,WebSocket.OPEN);
  }finally{await ctx.close();}
});

test('screen budget is shared across rooms and viewers; presenter loss frees the session',async()=>{
  const ctx=await setup();try{
    const r=await room(ctx.service),r2=await room(ctx.service),a=await join(ctx,r),b=await join(ctx,r),c=await join(ctx,r),other=await join(ctx,r2),session=await claim(a);await claim(other);
    b.send({type:'screen:watch',session});await b.next(m=>m.type==='screen:watching');c.send({type:'screen:watch',session});await c.next(m=>m.type==='screen:watching');
    assert.equal(ctx.service.screens.state(r.id).maxBitrate,2000000);
    const released=b.next(m=>m.type==='screen:state'&&!m.owner,true);a.socket.close();await released;assert.equal(ctx.service.screens.shares.has(r.id),false);assert.equal(ctx.service.screens.state(r2.id).maxBitrate,4500000);
  }finally{await ctx.close();}
});

test('TURN REST credentials are temporary, member scoped, HMAC authenticated and absent without configuration',()=>{
  const config=screenConfiguration({TURN_URLS:'turn:voicebridge-room.cn:3478?transport=udp,turns:voicebridge-room.cn:5349?transport=tcp',TURN_SHARED_SECRET:'secret-for-test-only-'.repeat(3),STUN_URLS:'stun:voicebridge-room.cn:3478'}),now=1792630000000;
  const result=screenIce(config,'room','member',now),turn=result.iceServers[1];assert.equal(result.expires,1792630600);assert.equal(turn.username,'1792630600:room:member');assert.equal(turn.credential,createHmac('sha1',config.secret).update(turn.username).digest('base64'));assert.equal(result.relayAvailable,true);
  assert.equal(JSON.stringify(result).includes(config.secret),false);assert.equal(screenIce(screenConfiguration({}),'room','member').relayAvailable,false);
  assert.throws(()=>screenConfiguration({TURN_URLS:'turn:example.com:3478'}));assert.throws(()=>screenConfiguration({TURN_URLS:'https://evil.example',TURN_SHARED_SECRET:'a'.repeat(40)}));
});
