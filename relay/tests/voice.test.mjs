import test from 'node:test';
import assert from 'node:assert/strict';
import {createHash,randomBytes} from 'node:crypto';
import {once} from 'node:events';
import WebSocket from 'ws';
import {startServer} from '../server.mjs';
const publisher='1'.repeat(64),hash=createHash('sha256').update(publisher).digest('hex');
async function room(service){const response=await fetch(service.url+'/api/rooms',{method:'POST',headers:{Authorization:'Bearer '+publisher},body:JSON.stringify({player:'test'})});assert.equal(response.status,201);return response.json();}
function messages(ws){const items=[],waiters=[];ws.on('message',(data,binary)=>{const value=binary?data:JSON.parse(data);const n=waiters.findIndex(w=>w.predicate(value));if(n>=0)waiters.splice(n,1)[0].resolve(value);else items.push(value);});return predicate=>{const n=items.findIndex(predicate);if(n>=0)return Promise.resolve(items.splice(n,1)[0]);return new Promise((resolve,reject)=>{const waiter={predicate,resolve:value=>{clearTimeout(timeout);resolve(value);}};const timeout=setTimeout(()=>{waiters.splice(waiters.indexOf(waiter),1);reject(new Error('Message timeout'));},3000);waiters.push(waiter);});};}
async function join(service,r,name='朋友'){const ws=new WebSocket(service.url.replace('http:','ws:')+`/api/rooms/${r.id}/voice`,{origin:service.url});const next=messages(ws);await once(ws,'open');ws.send(JSON.stringify({type:'join',token:r.listenToken,name}));const welcome=await next(m=>m.type==='welcome');return {ws,next,id:welcome.id};}
async function frame(r,id,sequence,rawKey){const base=await crypto.subtle.importKey('raw',rawKey,'HKDF',false,['deriveKey']);const key=await crypto.subtle.deriveKey({name:'HKDF',hash:'SHA-256',salt:new TextEncoder().encode(r.id),info:new TextEncoder().encode('VoiceBridge voice v1|'+id)},base,{name:'AES-GCM',length:256},false,['encrypt','decrypt']);const pcm=Buffer.alloc(640);for(let i=0;i<320;i++)pcm.writeInt16LE(Math.round(Math.sin(i*.15)*1000),i*2);const nonce=randomBytes(12);const ciphertext=await crypto.subtle.encrypt({name:'AES-GCM',iv:nonce,additionalData:new TextEncoder().encode(r.id+'|voice|'+id+'|'+sequence)},key,pcm);const data=Buffer.alloc(676);data.write('VBV1');data.writeUInt32LE(sequence,4);nonce.copy(data,8);Buffer.from(ciphertext).copy(data,20);return {data,key,pcm};}
test('encrypted voice preserves PCM, excludes sender and other rooms, mute and revocation close access',async()=>{
  const service=await startServer({port:0,host:'127.0.0.1',publishKeyHash:hash});const clients=[];
  try{
    const r=await room(service),other=await room(service);const a=await join(service,r,'主持人'),b=await join(service,r,'朋友'),c=await join(service,other,'另一个房间');clients.push(a.ws,b.ws,c.ws);
    let echoed=0,leaked=0;a.ws.on('message',(_,binary)=>{if(binary)echoed++;});c.ws.on('message',(_,binary)=>{if(binary)leaked++;});
    a.ws.send(JSON.stringify({type:'microphone',enabled:true}));const source=await frame(r,a.id,0,randomBytes(32));a.ws.send(source.data);
    const received=await b.next(Buffer.isBuffer);assert.equal(received.length,692);assert.equal(received.subarray(0,16).toString('hex'),a.id);
    const plain=await crypto.subtle.decrypt({name:'AES-GCM',iv:received.subarray(24,36),additionalData:new TextEncoder().encode(r.id+'|voice|'+a.id+'|0')},source.key,received.subarray(36));assert.deepEqual(Buffer.from(plain),source.pcm);assert.equal(echoed,0);assert.equal(leaked,0);
    const stopped=once(a.ws,'close');a.ws.send(JSON.stringify({type:'microphone',enabled:false}));a.ws.send((await frame(r,a.id,1,randomBytes(32))).data);assert.equal((await stopped)[0],4003);
    const revoked=once(b.ws,'close');const response=await fetch(service.url+`/api/rooms/${r.id}`,{method:'DELETE',headers:{Authorization:'Bearer '+r.writeToken}});assert.equal(response.status,200);assert.equal((await revoked)[0],4004);
  }finally{clients.forEach(ws=>ws.terminate());await service.stop();}
});
test('invalid invitations, replayed frames and foreign web origins are rejected',async()=>{
  const service=await startServer({port:0,host:'127.0.0.1',publishKeyHash:hash});const clients=[];
  try{
    const r=await room(service);const bad=new WebSocket(service.url.replace('http:','ws:')+`/api/rooms/${r.id}/voice`,{origin:service.url});clients.push(bad);await once(bad,'open');const denied=once(bad,'close');bad.send(JSON.stringify({type:'join',token:'0'.repeat(64)}));assert.equal((await denied)[0],4003);
    const a=await join(service,r);clients.push(a.ws);a.ws.send(JSON.stringify({type:'microphone',enabled:true}));const f=await frame(r,a.id,0,randomBytes(32));a.ws.send(f.data);const replay=once(a.ws,'close');a.ws.send(f.data);assert.equal((await replay)[0],4003);
    const foreign=new WebSocket(service.url.replace('http:','ws:')+`/api/rooms/${r.id}/voice`,{origin:'https://foreign.example'});await assert.rejects(once(foreign,'open'));foreign.terminate();
  }finally{clients.forEach(ws=>ws.terminate());await service.stop();}
});
test('room is bounded to eight members and listen-only pages cannot request microphone',async()=>{
  const service=await startServer({port:0,host:'127.0.0.1',publishKeyHash:hash});const clients=[];
  try{const r=await room(service);for(let i=0;i<8;i++)clients.push((await join(service,r)).ws);
    const extra=new WebSocket(service.url.replace('http:','ws:')+`/api/rooms/${r.id}/voice`,{origin:service.url});clients.push(extra);await once(extra,'open');const full=once(extra,'close');extra.send(JSON.stringify({type:'join',token:r.listenToken}));assert.equal((await full)[0],4009);
    const listen=await fetch(service.url+`/listen/${r.id}`),page=await fetch(service.url+`/room/${r.id}`);assert.match(listen.headers.get('Permissions-Policy'),/microphone=\(\)/);assert.match(page.headers.get('Permissions-Policy'),/microphone=\(self\)/);assert.match(await page.text(),/进入房间（麦克风关闭）/);
  }finally{clients.forEach(ws=>ws.terminate());await service.stop();}
});
