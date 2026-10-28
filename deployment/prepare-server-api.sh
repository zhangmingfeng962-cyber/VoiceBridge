#!/bin/bash
set -euo pipefail
umask 077
node=/opt/voicebridge/runtime/node-v22.23.3-linux-x64/bin/node
release=/opt/voicebridge/releases/0.10.0-web1
install -d -m 755 "$release/relay"
payload=$(mktemp)
trap 'rm -f "$payload"' EXIT
curl --fail --location --silent --show-error --retry 2 --max-time 90 'https://api.github.com/repos/zhangmingfeng962-cyber/VoiceBridge/contents/deployment/relay-0.10.0-source.b64?ref=cc4fa4f0dc4f394d6270a20f18cad8ece428af3b' > "$payload"
"$node" - "$payload" "$release/relay" <<'NODE'
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),zlib=require('node:zlib');
const response=JSON.parse(fs.readFileSync(process.argv[2],'utf8'));
if(response.encoding!=='base64'||typeof response.content!=='string')throw Error('Unexpected GitHub API response');
const raw=Buffer.from(Buffer.from(response.content.replace(/\s/g,''),'base64').toString('utf8').trim(),'base64');
if(crypto.createHash('sha256').update(raw).digest('hex')!=='0a8d69ee170512a7d418a61d2e5cb5ff39d2955933eb423452263cb7ef94ae80')throw Error('Bundle integrity mismatch');
const files=JSON.parse(zlib.gunzipSync(raw,{maxOutputLength:5000000})),root=process.argv[3];
if(Object.keys(files).length!==32)throw Error('Bundle file count mismatch');
for(const [name,encoded] of Object.entries(files)){
 const target=path.resolve(root,name);if(!target.startsWith(root+'/')||name.includes('..')||name.startsWith('/'))throw Error('Invalid source path');
 fs.mkdirSync(path.dirname(target),{recursive:true});fs.writeFileSync(target,Buffer.from(encoded,'base64'),{mode:0o644});
}
console.log('SOURCE_INTEGRITY_OK 32 files');
NODE
cat > "$release/relay/ecs-launcher.mjs" <<'NODE'
import {startServer} from './server.mjs';
const running=await startServer({host:'127.0.0.1'});
for(const signal of ['SIGINT','SIGTERM'])process.once(signal,()=>void running.stop().then(()=>process.exit()));
NODE
chmod 644 "$release/relay/ecs-launcher.mjs"
"$node" --check "$release/relay/server.mjs"
"$node" --input-type=module - "$release/relay" <<'NODE'
import {pathToFileURL} from 'node:url';import assert from 'node:assert/strict';import {createHash} from 'node:crypto';import {once} from 'node:events';
const root=process.argv[2],{startServer}=await import(pathToFileURL(root+'/server.mjs')),{default:WebSocket}=await import(pathToFileURL(root+'/node_modules/ws/wrapper.mjs'));
const run=await startServer({host:'127.0.0.1',port:3100,origin:'http://127.0.0.1:3100',publishKeyHash:createHash('sha256').update('staging-only').digest('hex'),publicRooms:true}),clients=[];
const box=s=>{const values=[],waiters=[];s.on('message',(data,binary)=>{const value=binary?data:JSON.parse(data),i=waiters.findIndex(w=>w.match(value));if(i<0)values.push(value);else waiters.splice(i,1)[0].done(value);});return match=>{const i=values.findIndex(match);if(i>=0)return Promise.resolve(values.splice(i,1)[0]);return new Promise((resolve,reject)=>{const timer=setTimeout(()=>reject(Error('Message timeout')),4000);waiters.push({match,done:v=>{clearTimeout(timer);resolve(v);}});});};};
try{
 const html=await(await fetch(run.url+'/')).text();assert.match(html,/渝ICP备2026026961号-1/);assert.match(html,/下载 Windows 客户端/);console.log('HOME_ICP_OK');
 const health=await(await fetch(run.url+'/health')).json();assert.equal(health.release,'0.10.0');assert.equal(health.screenShare,1);console.log('HEALTH_OK');
 const created=await fetch(run.url+'/api/rooms',{method:'POST',body:'{}'});assert.equal(created.status,201);const room=await created.json();console.log('ROOM_CREATE_OK');
 for(let i=0;i<2;i++){const socket=new WebSocket(run.url.replace('http:','ws:')+'/api/rooms/'+room.id+'/voice',{origin:run.url}),next=box(socket);clients.push(socket);socket.next=next;await once(socket,'open');socket.send(JSON.stringify({type:'join',version:3,role:'browser',token:room.listenToken,name:'Staging check',...(i===0?{hostToken:room.writeToken}:{})}));await next(m=>m.type==='welcome');}
 console.log('WS_JOIN_OK');
 clients[0].send(JSON.stringify({type:'flags',microphone:false,music:true}));const packet=Buffer.alloc(150);packet.write('VBA2');packet[4]=1;clients[0].send(packet);const received=await clients[1].next(Buffer.isBuffer);assert.deepEqual(received.subarray(16),packet);console.log('BINARY_FORWARD_BYTE_EXACT_OK');
 assert.equal((await fetch(run.url+'/api/rooms/'+room.id,{method:'DELETE',headers:{Authorization:'Bearer '+room.writeToken}})).status,200);console.log('ROOM_DELETE_OK');
}finally{clients.forEach(s=>s.terminate());await run.stop();}
NODE
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install --yes --no-install-recommends certbot >/var/log/voicebridge-certbot-install.log 2>&1
install -d -m 755 /var/www/voicebridge-acme/.well-known/acme-challenge
install -d -m 700 /etc/voicebridge/backups
cp -an /etc/nginx/nginx.conf /etc/voicebridge/backups/nginx-pre-public-20261028.conf
printf 'CERTBOT_READY '; certbot --version
printf 'PUBLIC_LISTENERS_UNCHANGED\n'; ss -ltn | awk 'NR==1 || /:(80|443|3000|8080) /'
printf 'PREPARATION_COMPLETE\n'
