import {randomBytes} from 'node:crypto';
import {createRequire} from 'node:module';
import dgram from 'node:dgram';
import {execFile} from 'node:child_process';
import {startServer} from '../relay/server.mjs';
import {screenConfiguration} from '../relay/screen-room.mjs';
const require=createRequire(new URL('../relay/server.mjs',import.meta.url)),{WebSocket}=require('ws');
// This STUN responder listens on loopback only and is used only by the local preview.
const stun=dgram.createSocket('udp4');stun.on('message',(request,remote)=>{
  if(request.length<20||request.readUInt16BE(0)!==1||request.readUInt32BE(4)!==0x2112a442)return;
  const response=Buffer.alloc(32);response.writeUInt16BE(0x101,0);response.writeUInt16BE(12,2);request.copy(response,4,4,20);response.writeUInt16BE(0x20,20);response.writeUInt16BE(8,22);response[25]=1;response.writeUInt16BE(remote.port^0x2112,26);
  const ip=remote.address.split('.').reduce((n,b)=>(n*256+Number(b))>>>0,0);response.writeUInt32BE((ip^0x2112a442)>>>0,28);stun.send(response,remote.port,remote.address);
});
await new Promise(resolve=>stun.bind(0,'127.0.0.1',resolve));
const service=await startServer({host:'127.0.0.1',port:4100,publicRooms:true,publishKeyHash:randomBytes(32).toString('hex'),screenConfig:screenConfiguration({STUN_URLS:'stun:127.0.0.1:'+stun.address().port})});
const room=await(await fetch(service.url+'/api/rooms',{method:'POST',body:'{}'})).json(),key=randomBytes(32).toString('hex');
const host=new WebSocket(service.url.replace('http:','ws:')+`/api/rooms/${room.id}/voice`,{origin:service.url});
host.on('open',()=>host.send(JSON.stringify({type:'join',version:3,role:'browser',token:room.listenToken,hostToken:room.writeToken,name:'本机预览服务'})));
host.on('message',(data,binary)=>{if(binary)return;const message=JSON.parse(data);if(message.type==='welcome'){const link=`${service.url}/room/${room.id}#${room.listenToken}.${key}`;console.log('本机预览房间已启动（仅本机访问，视频中继未公开）。');console.log('在 Edge / Chrome 打开以下链接，点击进入房间，再选择需要共享的窗口：');console.log(link);console.log('可在另一个标签页打开相同链接测试观看。按 Ctrl+C 结束。');if(process.platform==='win32'&&process.argv.includes('--open'))execFile('rundll32.exe',['url.dll,FileProtocolHandler',link],{windowsHide:true},error=>{if(error)console.log('请手动打开上面的本机链接。');});}});
let closing=false;async function stop(){if(closing)return;closing=true;clearTimeout(expire);host.terminate();stun.close();await service.stop();}
host.on('error',error=>{console.error(error.message);void stop();});
for(const signal of ['SIGINT','SIGTERM'])process.once(signal,()=>void stop());
const expire=setTimeout(()=>void stop(),2*60*60*1000);
