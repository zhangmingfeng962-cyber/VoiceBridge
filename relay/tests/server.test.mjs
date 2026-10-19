import test from 'node:test';import assert from 'node:assert/strict';import {createHash,randomBytes} from 'node:crypto';import {once} from 'node:events';import WebSocket from 'ws';import {startServer} from '../server.mjs';
const master='1'.repeat(64),hash=createHash('sha256').update(master).digest('hex');
async function create(service){const r=await fetch(service.url+'/api/rooms',{method:'POST',headers:{Authorization:'Bearer '+master},body:'{}'});assert.equal(r.status,201);return r.json();}
function mailbox(ws){const stored=[],waiting=[];ws.on('message',(data,binary)=>{const value=binary?data:JSON.parse(data);const i=waiting.findIndex(w=>w.match(value));if(i>=0)waiting.splice(i,1)[0].resolve(value);else stored.push(value);});return match=>{const index=stored.findIndex(match);if(index>=0)return Promise.resolve(stored.splice(index,1)[0]);return new Promise((resolve,reject)=>{const item={match,resolve:v=>{clearTimeout(timer);resolve(v);}},timer=setTimeout(()=>{waiting.splice(waiting.indexOf(item),1);reject(new Error('Message timeout'));},2000);waiting.push(item);});};}
async function join(service,room,role='browser',extra={}){const socket=new WebSocket(service.url.replace('http:','ws:')+`/api/rooms/${room.id}/voice`,{origin:service.url}),next=mailbox(socket);await once(socket,'open');socket.send(JSON.stringify({type:'join',version:3,role,token:room.listenToken,name:role,...extra}));const welcome=await next(m=>m.type==='welcome');return {socket,next,...welcome};}
function audio(kind=1,sequence=0){const data=Buffer.alloc(150);data.write('VBA2');data[4]=kind;data.writeUInt32LE(sequence,5);randomBytes(12).copy(data,9);return data;}
test('all members may share music; audio excludes sender, controller sockets and other rooms',async()=>{const service=await startServer({port:0,host:'127.0.0.1',publishKeyHash:hash});const clients=[];try{
 const room=await create(service),other=await create(service);const a=await join(service,room,'desktop'),b=await join(service,room),c=await join(service,other);clients.push(a.socket,b.socket,c.socket);
 const controller=await join(service,room,'controller',{memberId:a.id,controlToken:a.controlToken});clients.push(controller.socket);let echoes=0,leaks=0,controllerAudio=0;a.socket.on('message',(_,binary)=>{if(binary)echoes++;});c.socket.on('message',(_,binary)=>{if(binary)leaks++;});controller.socket.on('message',(_,binary)=>{if(binary)controllerAudio++;});
 a.socket.send(JSON.stringify({type:'flags',microphone:false,music:true,source:'Chrome'}));a.socket.send(audio());const received=await b.next(Buffer.isBuffer);assert.equal(received.subarray(0,16).toString('hex'),a.id);
 b.socket.send(JSON.stringify({type:'flags',microphone:false,music:true,source:'WeChat'}));b.socket.send(audio());assert.equal((await a.next(Buffer.isBuffer)).subarray(0,16).toString('hex'),b.id);assert.equal(leaks,0);assert.equal(controllerAudio,0);assert.equal(echoes,1); // a received only b's frame.
}finally{clients.forEach(s=>s.terminate());await service.stop();}});
test('a room invitation grants no control over another desktop and encrypted state remains private',async()=>{const service=await startServer({port:0,host:'127.0.0.1',publishKeyHash:hash});const clients=[];try{
 const room=await create(service),desktop=await join(service,room,'desktop'),friend=await join(service,room);clients.push(desktop.socket,friend.socket);let stateLeaks=0;friend.socket.on('message',(data,binary)=>{if(!binary&&JSON.parse(data).type==='state')stateLeaks++;});
 const controller=await join(service,room,'controller',{memberId:desktop.id,controlToken:desktop.controlToken});clients.push(controller.socket);const attached=await desktop.next(m=>m.type==='controllerAttached');assert.equal(attached.connectionId,controller.connectionId);
 const packet={type:'control',sequence:0,iv:'A'.repeat(16),data:'B'.repeat(80)};controller.socket.send(JSON.stringify(packet));assert.equal((await desktop.next(m=>m.type==='control')).connectionId,controller.connectionId);
 desktop.socket.send(JSON.stringify({type:'state',connectionId:controller.connectionId,sequence:0,iv:'C'.repeat(16),data:'D'.repeat(200)}));assert.equal((await controller.next(m=>m.type==='state')).data,'D'.repeat(200));assert.equal(stateLeaks,0);
 const bad=new WebSocket(service.url.replace('http:','ws:')+`/api/rooms/${room.id}/voice`,{origin:service.url});clients.push(bad);await once(bad,'open');const denied=once(bad,'close');bad.send(JSON.stringify({type:'join',version:3,role:'controller',memberId:desktop.id,controlToken:room.listenToken}));assert.equal((await denied)[0],4003);
 const detached=desktop.next(m=>m.type==='controllerDetached');controller.socket.close();await detached;
}finally{clients.forEach(s=>s.terminate());await service.stop();}});
test('owner disconnect revokes the room and muted queued frames are safely discarded',async()=>{const service=await startServer({port:0,host:'127.0.0.1',publishKeyHash:hash});const clients=[];try{
 const room=await create(service),owner=await join(service,room,'desktop',{hostToken:room.writeToken}),friend=await join(service,room);clients.push(owner.socket,friend.socket);let audioFrames=0;friend.socket.on('message',(_,binary)=>{if(binary)audioFrames++;});owner.socket.send(audio(0));owner.socket.send(JSON.stringify({type:'flags',microphone:false,music:true}));owner.socket.send(audio(1));await friend.next(Buffer.isBuffer);assert.equal(audioFrames,1);
 const closed=once(friend.socket,'close');owner.socket.close();assert.equal((await closed)[0],4004);const result=await fetch(service.url+'/api/rooms/'+room.id,{method:'DELETE',headers:{Authorization:'Bearer '+room.writeToken}});assert.equal(result.status,404);
}finally{clients.forEach(s=>s.terminate());await service.stop();}});
test('room limit, replay and foreign origins are enforced',async()=>{const service=await startServer({port:0,host:'127.0.0.1',publishKeyHash:hash});const clients=[];try{
 const room=await create(service);const a=await join(service,room);clients.push(a.socket);for(let n=0;n<7;n++)clients.push((await join(service,room)).socket);
 const extra=new WebSocket(service.url.replace('http:','ws:')+`/api/rooms/${room.id}/voice`,{origin:service.url});clients.push(extra);await once(extra,'open');const denied=once(extra,'close');extra.send(JSON.stringify({type:'join',version:3,role:'browser',token:room.listenToken}));assert.equal((await denied)[0],4009);
 a.socket.send(JSON.stringify({type:'flags',microphone:false,music:true}));const repeated=audio();a.socket.send(repeated);const replay=once(a.socket,'close');a.socket.send(repeated);assert.equal((await replay)[0],4003);
 const foreign=new WebSocket(service.url.replace('http:','ws:')+`/api/rooms/${room.id}/voice`,{origin:'https://foreign.example'});await assert.rejects(once(foreign,'open'));foreign.terminate();
}finally{clients.forEach(s=>s.terminate());await service.stop();}});
test('public build creates rooms without a sender key, with bounded creation and owner-only deletion',async()=>{
 const disabled=await startServer({port:0,host:'127.0.0.1',publishKeyHash:hash});try{assert.equal((await fetch(disabled.url+'/api/rooms',{method:'POST',body:'{}'})).status,403);}finally{await disabled.stop();}
 const service=await startServer({port:0,host:'127.0.0.1',publishKeyHash:hash,publicRooms:true});try{
 const response=await fetch(service.url+'/api/rooms',{method:'POST',body:'{}'});assert.equal(response.status,201);const room=await response.json();assert.equal((await fetch(service.url+`/api/rooms/${room.id}`,{method:'DELETE'})).status,403);
 assert.equal((await fetch(service.url+`/api/rooms/${room.id}`,{method:'DELETE',headers:{Authorization:'Bearer '+room.writeToken}})).status,200);
 for(let n=0;n<3;n++)assert.equal((await fetch(service.url+'/api/rooms',{method:'POST',body:'{}'})).status,201);
 const limited=await fetch(service.url+'/api/rooms',{method:'POST',body:'{}'});assert.equal(limited.status,429);assert.equal(limited.headers.get('retry-after'),'60');
 }finally{await service.stop();}
});
test('browser and paired controller rename only themselves; first controller preserves automatic microphone, replacement mutes',async()=>{
 const service=await startServer({port:0,host:'127.0.0.1',publishKeyHash:hash}),clients=[];try{
 const room=await create(service),desktop=await join(service,room,'desktop'),friend=await join(service,room);clients.push(desktop.socket,friend.socket);
 desktop.socket.send(JSON.stringify({type:'flags',microphone:true,music:false}));await friend.next(m=>m.type==='members'&&m.members.some(p=>p.id===desktop.id&&p.microphone));
 const controller=await join(service,room,'controller',{memberId:desktop.id,controlToken:desktop.controlToken});clients.push(controller.socket);assert.equal(service.peers.get(desktop.id).microphone,true);
 controller.socket.send(JSON.stringify({type:'rename',name:'主持人新昵称',memberId:friend.id}));let roster=await friend.next(m=>m.type==='members'&&m.members.some(p=>p.name==='主持人新昵称'));assert.equal(roster.members.find(p=>p.id===friend.id).name,'browser');
 friend.socket.send(JSON.stringify({type:'rename',name:'朋友改名'}));roster=await controller.next(m=>m.type==='members'&&m.members.some(p=>p.name==='朋友改名'));assert.equal(roster.members.find(p=>p.id===desktop.id).name,'主持人新昵称');
 const replacement=await join(service,room,'controller',{memberId:desktop.id,controlToken:desktop.controlToken});clients.push(replacement.socket);assert.equal(service.peers.get(desktop.id).microphone,false);
 }finally{clients.forEach(s=>s.terminate());await service.stop();}
});
