import {randomBytes} from 'node:crypto';
import {WebSocketServer,WebSocket} from 'ws';
import {authorizeRoom} from './worker/index.js';

// A bounded live fan-out. Only ciphertext is forwarded, never recorded or decoded.
export function attachVoiceHub(server,env,origin){
  const wss=new WebSocketServer({noServer:true,maxPayload:2048,perMessageDeflate:false});
  const peers=new Map();let closed=false;
  const send=(ws,data,binary=false)=>{if(ws.readyState!==WebSocket.OPEN)return;if(ws.bufferedAmount>65536){ws.close(4008,'Connection too slow');return;}ws.send(data,{binary});};
  const roster=room=>JSON.stringify({type:'members',members:[...peers.values()].filter(p=>p.room===room).map(p=>({id:p.id,name:p.name,microphone:p.microphone}))});
  const announce=room=>{const message=roster(room);for(const p of peers.values())if(p.room===room)send(p.ws,message);};
  const check=async(room,token)=>authorizeRoom(new Request(`https://relay.invalid/api/rooms/${room}`,{headers:{Authorization:'Bearer '+token}}),env,{waitUntil:()=>{}});
  server.on('upgrade',(req,socket,head)=>{
    const match=req.url?.match(/^\/api\/rooms\/([a-f0-9]{32})\/voice$/);
    const expected=origin||`http://127.0.0.1:${server.address().port}`;
    if(closed||!match||req.headers.origin!==expected||wss.clients.size>=32){socket.end('HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n');return;}
    wss.handleUpgrade(req,socket,head,ws=>wss.emit('connection',ws,match[1]));
  });
  wss.on('connection',(ws,room)=>{
    let peer,joining=false,alive=true,frames=0,controls=0,windowStart=Date.now();
    const timeout=setTimeout(()=>ws.close(4003,'Join required'),5000);timeout.unref();
    ws.on('error',()=>{});ws.on('pong',()=>{alive=true;});
    ws.on('close',()=>{clearTimeout(timeout);if(peer){peers.delete(peer.id);announce(room);}});
    ws.on('message',async(data,binary)=>{
      try{
        if(Date.now()-windowStart>=1000){frames=controls=0;windowStart=Date.now();}
        if(!peer){
          if(joining||binary||++controls>2)throw new Error('Join');joining=true;
          const input=JSON.parse(data.toString());
          if(input.type!=='join'||typeof input.token!=='string')throw new Error('Join');
          await check(room,input.token);
          if(ws.readyState!==WebSocket.OPEN)return;
          if([...peers.values()].filter(p=>p.room===room).length>=8){ws.close(4009,'Room full');return;}
          const name=typeof input.name==='string'?input.name.replace(/[\p{C}]/gu,'').trim().slice(0,20):'';
          peer={id:randomBytes(16).toString('hex'),room,ws,token:input.token,name:name||'朋友',microphone:false,lastSeq:-1};
          peers.set(peer.id,peer);clearTimeout(timeout);send(ws,JSON.stringify({type:'welcome',id:peer.id,maxMembers:8}));announce(room);return;
        }
        if(!binary){
          if(++controls>10)throw new Error('Rate');const message=JSON.parse(data.toString());
          if(message.type!=='microphone'||typeof message.enabled!=='boolean')throw new Error('Control');
          peer.microphone=message.enabled;announce(room);return;
        }
        if(++frames>80||!peer.microphone||data.length!==676||data.readUInt32LE(0)!==0x31564256)throw new Error('Frame');
        const sequence=data.readUInt32LE(4);if(sequence<=peer.lastSeq)throw new Error('Replay');peer.lastSeq=sequence;
        const frame=Buffer.concat([Buffer.from(peer.id,'hex'),data]);
        for(const target of peers.values())if(target.room===room&&target.id!==peer.id)send(target.ws,frame,true);
      }catch{ws.close(4003,'Invalid room message');}
    });
    // Store heartbeat state on the socket only; never log invitations or audio.
    ws.voiceHeartbeat=()=>{if(!alive){ws.terminate();return;}alive=false;ws.ping();};
  });
  const reconcile=async()=>{for(const peer of [...peers.values()]){try{await check(peer.room,peer.token);}catch{peer.ws.close(4004,'Room closed');}}};
  const expiry=setInterval(()=>void reconcile(),2000);expiry.unref();
  const heartbeat=setInterval(()=>{for(const ws of wss.clients)ws.voiceHeartbeat?.();},20000);heartbeat.unref();
  return {reconcile,stop(){closed=true;clearInterval(expiry);clearInterval(heartbeat);for(const ws of wss.clients)ws.terminate();wss.close();peers.clear();}};
}
