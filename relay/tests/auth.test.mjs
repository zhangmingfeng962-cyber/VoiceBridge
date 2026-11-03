import test from 'node:test';import assert from 'node:assert/strict';
import {mkdtemp,readFile,rm} from 'node:fs/promises';import os from 'node:os';import path from 'node:path';
import {createHash} from 'node:crypto';import {once} from 'node:events';import WebSocket from 'ws';
import {startServer} from './test-server.mjs';
import {createAuth} from '../auth.mjs';
const password='一起听歌 一起开黑 secure-2026',master='1'.repeat(64),publishKeyHash=createHash('sha256').update(master).digest('hex');
async function fixture(options={}){
 const directory=await mkdtemp(path.join(os.tmpdir(),'vb-auth-')),mail=[],clients=[];
 const service=await startServer({port:0,host:'127.0.0.1',publicRooms:true,publishKeyHash,authRequired:true,authOptions:{directory,freeRemaining:20,sendMail:async value=>mail.push(value),...options}});
 const call=async(route,body,token,method='POST')=>{const response=await fetch(service.url+route,{method,headers:{'Content-Type':'application/json','X-VoiceBridge-Client':'desktop',...(token?{Authorization:'Bearer '+token}:{})},...(method==='GET'?{}:{body:JSON.stringify(body||{})})});return {status:response.status,data:await response.json(),headers:response.headers};};
 async function verified(address,purpose='register'){assert.equal((await call('/api/auth/code',{address,purpose})).status,200);const code=mail.findLast(m=>m.address===address).code;const response=await call('/api/auth/verify',{address,purpose,code});assert.equal(response.status,200);return {ticket:response.data.verificationToken,code};}
 async function register(address='friend@example.com',nickname='朋友'){const {ticket}=await verified(address);const response=await call('/api/auth/register',{verificationToken:ticket,password,nickname});assert.equal(response.status,200,JSON.stringify(response.data));return response.data;}
 async function join(room,identity,extra={}){const socket=new WebSocket(service.url.replace('http:','ws:')+'/api/rooms/'+room.id+'/voice',{origin:service.url,headers:{Authorization:'Bearer '+identity.token}});clients.push(socket);const messages=[],waiters=[];socket.on('message',bytes=>{const message=JSON.parse(bytes);messages.push(message);for(const w of [...waiters])if(w.match(message)){waiters.splice(waiters.indexOf(w),1);w.done(message);}});const next=match=>{const found=messages.find(match);if(found)return Promise.resolve(found);return new Promise((resolve,reject)=>{const timer=setTimeout(()=>reject(Error('Auth test message timeout')),2500);waiters.push({match,done:value=>{clearTimeout(timer);resolve(value);}});});};await once(socket,'open');socket.send(JSON.stringify({type:'join',version:4,role:'browser',token:room.listenToken,name:'spoofed',canResume:true,...extra}));return {socket,next,...await next(m=>m.type==='welcome')};}
 return {service,mail,call,register,verified,join,directory,close:async()=>{for(const c of clients)c.terminate();await service.stop();if(!path.resolve(directory).startsWith(path.resolve(os.tmpdir())+path.sep+'vb-auth-'))throw Error('Unexpected test directory');await rm(directory,{recursive:true,force:true});}};
}
test('registration requires one-use email verification; password login and private profile persist',async()=>{
 const f=await fixture();try{
  assert.equal((await f.call('/api/auth/register',{password,nickname:'伪造'})).status,400);
  const {ticket,code}=await f.verified('friend@example.com');
  assert.equal((await f.call('/api/auth/verify',{address:'friend@example.com',purpose:'register',code})).status,400);
  const result=await f.call('/api/auth/register',{verificationToken:ticket,password,nickname:'玩家一'});assert.equal(result.status,200);const identity=result.data;
  assert.equal((await f.call('/api/auth/register',{verificationToken:ticket,password,nickname:'重用'})).status,400);
  assert.equal((await f.call('/api/auth/login',{address:'friend@example.com',password:'wrong password'})).status,401);
  const login=await f.call('/api/auth/login',{address:'friend@example.com',password});assert.equal(login.status,200);assert.equal(login.data.user.id,identity.user.id);assert.equal(login.data.user.nickname,'玩家一');assert.equal('email' in login.data.user,false);
  const profile=await f.call('/api/me',{nickname:'已改名'},login.data.token,'PATCH');assert.equal(profile.data.user.nickname,'已改名');
  const bytes=await readFile(path.join(f.directory,'accounts.sqlite-wal'));assert.equal(bytes.includes(Buffer.from(password)),false);
 }finally{await f.close();}
});
test('password reset revokes existing sessions and does not allow code reuse',async()=>{
 const f=await fixture();try{const user=await f.register(),other=await f.call('/api/auth/login',{address:'friend@example.com',password});const {ticket}=await f.verified('friend@example.com','reset');const nextPassword=password+' 新';assert.equal((await f.call('/api/auth/reset',{verificationToken:ticket,password:nextPassword})).status,200);assert.equal((await f.call('/api/me',null,user.token,'GET')).status,401);assert.equal((await f.call('/api/me',null,other.data.token,'GET')).status,401);assert.equal((await f.call('/api/auth/login',{address:'friend@example.com',password})).status,401);assert.equal((await f.call('/api/auth/login',{address:'friend@example.com',password:nextPassword})).status,200);assert.equal((await f.call('/api/auth/reset',{verificationToken:ticket,password})).status,400);}finally{await f.close();}
});
test('changing password retains current session, invalidates other sessions, and logout is final',async()=>{
 const f=await fixture();try{const user=await f.register(),other=(await f.call('/api/auth/login',{address:'friend@example.com',password})).data;assert.equal((await f.call('/api/auth/password',{currentPassword:'bad',newPassword:password+'!'},user.token)).status,401);assert.equal((await f.call('/api/auth/password',{currentPassword:password,newPassword:password+'!'},user.token)).status,200);assert.equal((await f.call('/api/me',null,user.token,'GET')).status,200);assert.equal((await f.call('/api/me',null,other.token,'GET')).status,401);await f.call('/api/auth/logout',{},user.token);assert.equal((await f.call('/api/me',null,user.token,'GET')).status,401);}finally{await f.close();}
});
test('mail quotas fail closed and wrong codes have a bounded attempt count',async()=>{
 const f=await fixture({freeRemaining:1});try{assert.equal((await f.call('/api/auth/code',{address:'a@example.com',purpose:'register'})).status,200);assert.equal((await f.call('/api/auth/code',{address:'b@example.com',purpose:'register'})).status,503);const real=f.mail[0].code,wrong=real==='000000'?'999999':'000000';for(let n=0;n<5;n++)assert.equal((await f.call('/api/auth/verify',{address:'a@example.com',purpose:'register',code:wrong})).status,400);assert.equal((await f.call('/api/auth/verify',{address:'a@example.com',purpose:'register',code:real})).status,400);}finally{await f.close();}
});
test('mandatory login guards creation/admission, member identity is server supplied, passive close resumes',async()=>{
 const f=await fixture();try{assert.equal((await f.call('/api/rooms',{name:'无登录'})).status,401);const user=await f.register(),created=await f.call('/api/rooms',{name:'账号房间',public:true,publicKey:'a'.repeat(64)},user.token);assert.equal(created.status,201);const room=created.data;
  assert.equal((await f.call('/api/lobby/rooms/'+room.id+'/join',{})).status,401);
  const owner=await f.join(room,user,{hostToken:room.writeToken});const roster=await owner.next(m=>m.type==='members');assert.equal(roster.members[0].name,'朋友');assert.equal(roster.members[0].userId,user.user.id);assert.equal(JSON.stringify(roster).includes('friend@example.com'),false);
  const closed=once(owner.socket,'close');owner.socket.close(1001);await closed;const resumed=await f.join(room,user,{resume:{memberId:owner.id,token:owner.resumeToken}});assert.equal(resumed.id,owner.id);assert.equal(resumed.resumed,true);
 }finally{await f.close();}
});
test('avatar validates decoded PNG and normalizes output; profile persists through a fresh account worker',async()=>{
 const f=await fixture();try{const user=await f.register();assert.equal((await f.call('/api/me/avatar',{png:Buffer.from('<svg/>').toString('base64')},user.token)).status,400);const {PNG}=await import('pngjs');const raw=new PNG({width:32,height:32});raw.data.fill(255);const upload=await f.call('/api/me/avatar',{png:PNG.sync.write(raw).toString('base64')},user.token);assert.equal(upload.status,200);const fresh=await createAuth({directory:f.directory,freeRemaining:0,sendMail:null});try{assert.equal((await fresh.rpc('who',{token:user.token})).user.avatarUrl,upload.data.user.avatarUrl);}finally{await fresh.stop();}const response=await fetch(f.service.url+upload.data.user.avatarUrl),bytes=Buffer.from(await response.arrayBuffer());assert.equal(bytes.readUInt32BE(16),256);assert.equal(bytes.readUInt32BE(20),256);}finally{await f.close();}
});

test('embedded screen ticket is one-use and bound to the signed-in native member',async()=>{
 const f=await fixture();let controller;try{
  const user=await f.register(),other=await f.register('other@example.com','其他账号');
  const room=(await f.call('/api/rooms',{name:'控制票据',publicKey:'a'.repeat(64)},user.token)).data;
  const native=await f.join(room,user,{role:'desktop',nativeControls:true,hostToken:room.writeToken});
  const scope={roomId:room.id,memberId:native.id};
  assert.equal((await f.call('/api/auth/desktop-ticket',scope,other.token)).status,403);
  const issued=await f.call('/api/auth/desktop-ticket',scope,user.token);assert.equal(issued.status,200);
  controller=new WebSocket(f.service.url.replace('http:','ws:')+'/api/rooms/'+room.id+'/voice',['voicebridge','vb-ticket.'+issued.data.ticket],{origin:f.service.url});
  const message=once(controller,'message');await once(controller,'open');
  assert.equal(controller.protocol,'voicebridge');
  controller.send(JSON.stringify({type:'join',version:4,role:'controller',memberId:native.id,controlToken:native.controlToken}));
  assert.equal(JSON.parse((await message)[0]).role,'controller');
  const repeated=new WebSocket(f.service.url.replace('http:','ws:')+'/api/rooms/'+room.id+'/voice',['voicebridge','vb-ticket.'+issued.data.ticket],{origin:f.service.url});
  const status=await new Promise((resolve,reject)=>{repeated.once('unexpected-response',(_req,res)=>{res.resume();resolve(res.statusCode);});repeated.once('open',()=>{repeated.terminate();reject(Error('Reused ticket accepted'));});repeated.once('error',()=>{});});
  assert.equal(status,401);assert.equal(f.service.peers.size,1);
 }finally{controller?.terminate();await f.close();}
});
