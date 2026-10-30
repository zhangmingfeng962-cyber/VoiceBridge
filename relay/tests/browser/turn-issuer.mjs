import {WebSocket} from 'ws';
import {once} from 'node:events';
import fs from 'node:fs';

// Test-only: obtain temporary credentials as ordinary room members. No server secret is read.
export async function turnIssuer(destination){
 const origin='https://voicebridge-room.cn',sockets=[],profiles=[null,null,null];let expires=0;
 const response=await fetch(origin+'/api/rooms',{method:'POST',body:'{}',signal:AbortSignal.timeout(15000)});
 if(response.status!==201)throw Error('TURN test room unavailable ('+response.status+')');const room=await response.json();
 try{
  for(let index=0;index<3;index++){
   const socket=new WebSocket(origin.replace('https:','wss:')+'/api/rooms/'+room.id+'/voice',{origin});sockets.push(socket);
   const ready=new Promise((resolve,reject)=>{const timer=setTimeout(()=>reject(Error('TURN credential timeout')),12000);socket.on('error',()=>{});socket.on('message',data=>{const message=JSON.parse(data.toString());if(message.type==='screen:hello'){profiles[index]=message.iceServers;expires=message.expires;if(profiles.every(Boolean))fs.writeFileSync(destination,JSON.stringify({profiles,expires}));clearTimeout(timer);resolve();}});});
   await once(socket,'open');socket.send(JSON.stringify({type:'join',version:3,role:'browser',token:room.listenToken,name:'VoiceBridge合成验证',...(index===0?{hostToken:room.writeToken}:{})}));await ready;
  }
  const timer=setInterval(()=>{for(const socket of sockets)if(socket.readyState===1)socket.send(JSON.stringify({type:'screen:ice'}));},180000);timer.unref();
  return {stop:async()=>{clearInterval(timer);for(const socket of sockets){if(socket.readyState===1)socket.send(JSON.stringify({type:'leave'}));socket.close();}await new Promise(resolve=>setTimeout(resolve,150));await fetch(origin+'/api/rooms/'+room.id,{method:'DELETE',headers:{Authorization:'Bearer '+room.writeToken},signal:AbortSignal.timeout(10000)}).catch(()=>{});}};
 }catch(error){sockets.forEach(socket=>socket.terminate());throw error;}
}
