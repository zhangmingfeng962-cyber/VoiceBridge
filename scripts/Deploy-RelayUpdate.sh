#!/usr/bin/env bash
set -euo pipefail
umask 077
commit=${1:?Pinned source commit required}
digest=${2:?Bundle SHA256 required}
[[ "$commit" =~ ^[a-f0-9]{40}$ && "$digest" =~ ^[a-f0-9]{64}$ ]] || exit 2
node=/opt/voicebridge/runtime/node-v22.23.3-linux-x64/bin/node
release=/opt/voicebridge/releases/0.11.0-web1
install -d -m 755 "$release/relay"
payload=$(mktemp)
trap 'rm -f "$payload"' EXIT
curl -fsSL --retry 2 --max-time 90 "https://api.github.com/repos/zhangmingfeng962-cyber/VoiceBridge/contents/deployment/relay-0.11.0-source.b64?ref=$commit" > "$payload"
"$node" - "$payload" "$release/relay" "$digest" <<'NODE'
const fs=require('node:fs'),path=require('node:path'),zlib=require('node:zlib'),crypto=require('node:crypto');
const response=JSON.parse(fs.readFileSync(process.argv[2],'utf8'));
if(response.encoding!=='base64')throw Error('Unexpected API encoding');
const compressed=Buffer.from(Buffer.from(response.content,'base64').toString('utf8').trim(),'base64');
if(crypto.createHash('sha256').update(compressed).digest('hex')!==process.argv[4])throw Error('Source integrity mismatch');
const files=JSON.parse(zlib.gunzipSync(compressed,{maxOutputLength:5000000})),root=process.argv[3];
if(Object.keys(files).length!==35)throw Error('Unexpected file count');
for(const [name,data] of Object.entries(files)){const target=path.resolve(root,name);if(!target.startsWith(root+'/')||name.includes('..'))throw Error('Unsafe source path');fs.mkdirSync(path.dirname(target),{recursive:true,mode:0o755});fs.writeFileSync(target,Buffer.from(data,'base64'),{mode:0o644});}
console.log('RELAY_SOURCE_SHA256_OK');
NODE
cat > "$release/relay/ecs-launcher.mjs" <<'NODE'
import {startServer} from './server.mjs';
const running=await startServer({host:'127.0.0.1'});
for(const signal of ['SIGINT','SIGTERM'])process.once(signal,()=>void running.stop().then(()=>process.exit()));
NODE
find "$release/relay" -type d -exec chmod 755 {} +
find "$release/relay" -type f -exec chmod 644 {} +
"$node" --check "$release/relay/server.mjs"
"$node" --input-type=module - "$release/relay" <<'NODE'
import {pathToFileURL} from 'node:url';import assert from 'node:assert/strict';import {createHash} from 'node:crypto';import {once} from 'node:events';
const root=process.argv[2],{startServer}=await import(pathToFileURL(root+'/server.mjs')),{default:WebSocket}=await import(pathToFileURL(root+'/node_modules/ws/wrapper.mjs'));
const service=await startServer({host:'127.0.0.1',port:3100,origin:'http://127.0.0.1:3100',publicRooms:true,publishKeyHash:createHash('sha256').update('1'.repeat(64)).digest('hex')}),clients=[];
function inbox(socket){const values=[],waiters=[];socket.on('message',(data,binary)=>{const value=binary?data:JSON.parse(data),index=waiters.findIndex(w=>w.match(value));if(index<0)values.push(value);else waiters.splice(index,1)[0].done(value);});return match=>{const index=values.findIndex(match);if(index>=0)return Promise.resolve(values.splice(index,1)[0]);return new Promise((resolve,reject)=>{const timer=setTimeout(()=>reject(Error('Staging message timeout')),5000);waiters.push({match,done:value=>{clearTimeout(timer);resolve(value);}});});};}
try{
 const health=await(await fetch(service.url+'/health')).json();assert.equal(health.release,'0.11.0');assert.equal(health.resume,true);assert.equal(health.roomTimeLimit,null);
 assert.match(await(await fetch(service.url+'/')).text(),/渝ICP备2026026961号-1/);assert.match(await(await fetch(service.url+'/room.css')).text(),/--accent/);
 const response=await fetch(service.url+'/api/rooms',{method:'POST',body:'{}'});assert.equal(response.status,201);const room=await response.json();
 async function join(extra={}){const socket=new WebSocket(service.url.replace('http:','ws:')+'/api/rooms/'+room.id+'/voice',{origin:service.url}),next=inbox(socket);clients.push(socket);await once(socket,'open');socket.send(JSON.stringify({type:'join',version:3,role:'browser',token:room.listenToken,canResume:true,name:'Staging synthetic',...extra}));return {socket,next,...await next(m=>m.type==='welcome')};}
 const owner=await join({hostToken:room.writeToken}),friend=await join();owner.socket.terminate();await friend.next(m=>m.type==='members'&&m.members.some(x=>x.id===owner.id&&!x.connected));assert.equal(friend.socket.readyState,1);
 const resumed=await join({resume:{memberId:owner.id,token:owner.resumeToken}});assert.equal(resumed.id,owner.id);
 resumed.socket.send(JSON.stringify({type:'flags',microphone:false,music:true}));const packet=Buffer.alloc(150);packet.write('VBA2');packet[4]=1;resumed.socket.send(packet);assert.deepEqual((await friend.next(Buffer.isBuffer)).subarray(16),packet);
 for(const socket of [resumed.socket,friend.socket]){const closed=once(socket,'close');socket.send(JSON.stringify({type:'leave'}));await closed;}
 assert.equal((await fetch(service.url+'/api/rooms/'+room.id,{method:'DELETE',headers:{Authorization:'Bearer '+room.writeToken}})).status,200);
 console.log('PRIVATE_STAGING_011_HEALTH_HOME_CSS_RESUME_AUDIO_EXIT_OK');
}finally{clients.forEach(s=>s.terminate());await service.stop();}
NODE
printf 'CANDIDATE_READY_0.11.0\n'
if [[ "${3:-}" != --activate ]]; then exit 0; fi
old=$(readlink -f /opt/voicebridge/current)
[[ "$old" == /opt/voicebridge/releases/* ]] || { echo 'Unexpected active release path'; exit 1; }
install -d -m 700 /etc/voicebridge/backups
if [[ ! -f /etc/voicebridge/backups/release-before-0.11.0 ]]; then printf '%s\n' "$old" > /etc/voicebridge/backups/release-before-0.11.0; fi
pending=/opt/voicebridge/current.pending-0.11.0-$$
ln -s "$release" "$pending"
mv -Tf "$pending" /opt/voicebridge/current
if ! systemctl restart voicebridge || ! curl -fsS --retry 10 --retry-connrefused --retry-delay 1 --max-time 5 http://127.0.0.1:3000/health | "$node" -e 'let s="";process.stdin.on("data",c=>s+=c);process.stdin.on("end",()=>{if(JSON.parse(s).release!=="0.11.0")process.exit(1);});'; then
  rollback=/opt/voicebridge/current.rollback-$$
  ln -s "$old" "$rollback";mv -Tf "$rollback" /opt/voicebridge/current
  systemctl restart voicebridge
  echo 'UPDATE_FAILED_ROLLED_BACK';exit 1
fi
systemctl is-active --quiet voicebridge nginx coturn voicebridge-turn-bandwidth
curl -fsS --resolve voicebridge-room.cn:443:127.0.0.1 https://voicebridge-room.cn/health
printf '\nPUBLIC_RELAY_0.11.0_ACTIVE\n'
