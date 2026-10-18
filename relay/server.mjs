import http from 'node:http';
import {readFile} from 'node:fs/promises';
import {randomBytes,createHash,timingSafeEqual} from 'node:crypto';
import {pathToFileURL} from 'node:url';
import {WebSocketServer,WebSocket} from 'ws';

const hex=n=>randomBytes(n).toString('hex'),hash=s=>createHash('sha256').update(s).digest('hex');
const valid=(value,n)=>typeof value==='string'&&new RegExp('^[a-f0-9]{'+n+'}$').test(value);
const equal=(a,b)=>typeof a==='string'&&typeof b==='string'&&a.length===b.length&&timingSafeEqual(Buffer.from(a),Buffer.from(b));
const headers={'Cache-Control':'no-store','Referrer-Policy':'no-referrer','X-Content-Type-Options':'nosniff','X-Frame-Options':'DENY','Permissions-Policy':'microphone=(self), camera=(), display-capture=(self)','Content-Security-Policy':"default-src 'none'; script-src 'self'; style-src 'unsafe-inline'; connect-src 'self'; worker-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'"};
const json=(res,status,data)=>{res.writeHead(status,{...headers,'Content-Type':'application/json; charset=utf-8'});res.end(JSON.stringify(data));};
const clean=(value,max)=>typeof value==='string'?value.replace(/[\p{C}]/gu,'').trim().slice(0,max):'';
export async function startServer({port=Number(process.env.PORT||3000),host='0.0.0.0',origin=process.env.PUBLIC_ORIGIN||process.env.RENDER_EXTERNAL_URL,publishKeyHash=process.env.PUBLISH_KEY_HASH}={}){
  if(!valid(publishKeyHash,64))throw new Error('Configure PUBLISH_KEY_HASH. Never publish the sender key.');
  const rooms=new Map(),peers=new Map();let stopped=false;
  const assets=new Map();for(const name of ['room.html','room.js','crypto.js','audio.js','room-worklet.js'])assets.set('/'+name,await readFile(new URL('./web/'+name,import.meta.url)));
  const send=(socket,value,binary=false)=>{if(socket.readyState!==WebSocket.OPEN)return;if(socket.bufferedAmount>65536){socket.close(4008,'Connection too slow');return;}socket.send(binary?value:JSON.stringify(value),{binary});};
  const roster=room=>({type:'members',members:[...peers.values()].filter(p=>p.room===room).map(p=>({id:p.id,name:p.name,desktop:p.role==='desktop',microphone:p.microphone,music:p.music,source:p.music?p.source:''}))});
  const announce=room=>{const data=roster(room);for(const p of peers.values())if(p.room===room){send(p.socket,data);if(p.controller)send(p.controller,data);}};
  const removeRoom=id=>{rooms.delete(id);for(const p of [...peers.values()])if(p.room===id){p.controller?.close(4004,'Room closed');p.socket.close(4004,'Room closed');peers.delete(p.id);}};
  const liveRoom=id=>{const room=rooms.get(id);if(!room)return null;if(room.expires<Date.now()||room.created+7200000<Date.now()){removeRoom(id);return null;}return room;};
  const readBody=async req=>{let size=0;const pieces=[];for await(const chunk of req){size+=chunk.length;if(size>2048)throw new Error('Body too large');pieces.push(chunk);}return Buffer.concat(pieces).toString();};
  const server=http.createServer(async(req,res)=>{
    try{
      const url=new URL(req.url,origin||`http://127.0.0.1:${server.address().port}`);
      if(req.headers.origin&&req.headers.origin!==url.origin){json(res,403,{error:'Origin rejected'});return;}
      if(req.method==='GET'&&url.pathname==='/health'){json(res,200,{service:'VoiceBridge encrypted music relay',protocol:3,version:3,ready:true,voiceRooms:true,codec:'opus',sampleRate:48000,musicBitrate:256000,frameMs:20});return;}
      if(req.method==='GET'&&(url.pathname==='/'||/^\/room\/[a-f0-9]{32}$/.test(url.pathname))){res.writeHead(200,{...headers,'Content-Type':'text/html; charset=utf-8'});res.end(assets.get('/room.html'));return;}
      if(req.method==='GET'&&assets.has(url.pathname)&&url.pathname.endsWith('.js')){res.writeHead(200,{...headers,'Content-Type':'text/javascript; charset=utf-8'});res.end(assets.get(url.pathname));return;}
      if(req.method==='POST'&&url.pathname==='/api/rooms'){
        const token=(req.headers.authorization||'').replace(/^Bearer /,'');if(!valid(token,64)||!equal(hash(token),publishKeyHash)){json(res,403,{error:'Sender credential invalid'});return;}
        await readBody(req);for(const id of [...rooms.keys()])liveRoom(id);if(rooms.size>=4){json(res,429,{error:'Too many active rooms'});return;}
        const id=hex(16),writeToken=hex(32),listenToken=hex(32);rooms.set(id,{id,writeHash:hash(writeToken),listenHash:hash(listenToken),created:Date.now(),expires:Date.now()+120000,host:null});json(res,201,{id,writeToken,listenToken,protocol:3});return;
      }
      const match=url.pathname.match(/^\/api\/rooms\/([a-f0-9]{32})$/);
      if(match&&req.method==='DELETE'){
        const room=liveRoom(match[1]),token=(req.headers.authorization||'').replace(/^Bearer /,'');if(!room){json(res,404,{error:'Room closed'});return;}
        if(!valid(token,64)||!equal(hash(token),room.writeHash)){json(res,403,{error:'Owner credential invalid'});return;}removeRoom(room.id);json(res,200,{stopped:true});return;
      }
      json(res,/^\/api\/rooms\//.test(url.pathname)?426:404,{error:'Please use VoiceBridge 0.9 or newer'});
    }catch{if(!res.headersSent)json(res,400,{error:'Invalid room request'});else res.end();}
  });
  const wss=new WebSocketServer({noServer:true,maxPayload:96000,perMessageDeflate:false});
  server.on('upgrade',(req,socket,head)=>{
    const match=req.url?.match(/^\/api\/rooms\/([a-f0-9]{32})\/voice$/),expected=origin||`http://127.0.0.1:${server.address().port}`;
    if(stopped||!match||req.headers.origin!==expected||wss.clients.size>=48||!liveRoom(match[1])){socket.end('HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n');return;}
    wss.handleUpgrade(req,socket,head,ws=>wss.emit('connection',ws,match[1]));
  });
  wss.on('connection',(socket,roomId)=>{
    let peer=null,controllerOf=null,connectionId=null,joined=false,alive=true,frames=[0,0],controls=0,start=Date.now(),lastControl=-1,lastState=-1;
    const deadline=setTimeout(()=>socket.close(4003,'Join required'),5000);deadline.unref();socket.on('error',()=>{});socket.on('pong',()=>{alive=true;});
    socket.on('close',()=>{
      clearTimeout(deadline);
      if(controllerOf&&controllerOf.controller===socket){controllerOf.controller=null;controllerOf.microphone=false;send(controllerOf.socket,{type:'controllerDetached'});announce(roomId);}
      if(peer){peers.delete(peer.id);peer.controller?.close(4004,'Desktop left');const room=rooms.get(roomId);if(room?.host===peer.id)removeRoom(roomId);else announce(roomId);}
    });
    socket.on('message',(data,binary)=>{
      try{
        if(Date.now()-start>=1000){start=Date.now();controls=0;frames=[0,0];}
        if(!joined){
          if(binary)throw new Error('Join required');const input=JSON.parse(data.toString()),room=liveRoom(roomId);if(!room||input.type!=='join'||input.version!==3)throw new Error('Upgrade required');
          if(input.role==='controller'){
            const target=peers.get(input.memberId);if(!target||target.room!==roomId||target.role!=='desktop'||!valid(input.controlToken,64)||!equal(input.controlToken,target.controlToken))throw new Error('Control capability invalid');
            target.controller?.close(4001,'Replaced by another control page');controllerOf=target;target.controller=socket;connectionId=hex(16);target.connectionId=connectionId;target.lastState=-1;target.microphone=false;
            joined=true;clearTimeout(deadline);send(socket,{type:'welcome',role:'controller',id:target.id,connectionId});send(target.socket,{type:'controllerAttached',connectionId});send(socket,roster(roomId));announce(roomId);return;
          }
          if(!['desktop','browser'].includes(input.role)||!valid(input.token,64)||!equal(hash(input.token),room.listenHash))throw new Error('Invitation invalid');
          if([...peers.values()].filter(p=>p.room===roomId).length>=8){socket.close(4009,'Room full');return;}
          const owner=valid(input.hostToken,64)&&equal(hash(input.hostToken),room.writeHash);if(owner&&room.host)throw new Error('Owner already joined');
          peer={id:hex(16),name:clean(input.name,20)||'朋友',room:roomId,role:input.role,socket,controller:null,controlToken:input.role==='desktop'?hex(32):null,connectionId:null,lastState:-1,microphone:false,music:false,source:'',lastSequences:[-1,-1]};
          peers.set(peer.id,peer);if(owner)room.host=peer.id;joined=true;clearTimeout(deadline);send(socket,{type:'welcome',role:peer.role,id:peer.id,...(peer.controlToken?{controlToken:peer.controlToken}:{})});announce(roomId);return;
        }
        if(!liveRoom(roomId)){socket.close(4004,'Room closed');return;}
        if(binary){
          if(!peer||data.length<38||data.length>1312||data.subarray(0,4).toString()!=='VBA2'||data[4]>1)throw new Error('Audio frame invalid');
          const kind=data[4],sequence=data.readUInt32LE(5);if(++frames[kind]>80||sequence<=peer.lastSequences[kind])throw new Error('Audio rate or replay');peer.lastSequences[kind]=sequence;
          if(kind===0&&!peer.microphone||kind===1&&!peer.music)return; // Safely discard a frame already queued when a user mutes.
          const packet=Buffer.concat([Buffer.from(peer.id,'hex'),data]);for(const target of peers.values())if(target.room===roomId&&target.id!==peer.id)send(target.socket,packet,true);return;
        }
        if(++controls>60)throw new Error('Control rate');const message=JSON.parse(data.toString());
        if(controllerOf){
          if(message.type!=='control'||!Number.isSafeInteger(message.sequence)||message.sequence<=lastControl||typeof message.iv!=='string'||message.iv.length!==16||typeof message.data!=='string'||message.data.length>64000)throw new Error('Control packet invalid');
          lastControl=message.sequence;send(controllerOf.socket,{...message,connectionId});return;
        }
        if(message.type==='state'&&peer.role==='desktop'){
          if(message.connectionId!==peer.connectionId)return;
          if(!Number.isSafeInteger(message.sequence)||message.sequence<=peer.lastState||typeof message.iv!=='string'||message.iv.length!==16||typeof message.data!=='string'||message.data.length>64000)throw new Error('State packet invalid');
          peer.lastState=message.sequence;if(peer.controller)send(peer.controller,message);return;
        }
        if(message.type==='flags'&&typeof message.microphone==='boolean'&&typeof message.music==='boolean'){
          peer.microphone=message.microphone;peer.music=message.music;peer.source=clean(message.source,60);announce(roomId);return;
        }
        throw new Error('Unknown room message');
      }catch{socket.close(4003,'Invalid room message');}
    });
    socket.roomHeartbeat=()=>{if(!alive){socket.terminate();return;}alive=false;socket.ping();};
  });
  const cleanup=setInterval(()=>{
    for(const room of rooms.values()){if(room.host&&peers.has(room.host))room.expires=Date.now()+120000;liveRoom(room.id);}
    for(const socket of wss.clients)socket.roomHeartbeat?.();
  },15000);cleanup.unref();
  server.headersTimeout=10000;server.requestTimeout=15000;server.maxConnections=80;
  await new Promise((resolve,reject)=>{server.once('error',reject);server.listen(port,host,resolve);});
  return {server,rooms,peers,url:`http://127.0.0.1:${server.address().port}`,stop:async()=>{stopped=true;clearInterval(cleanup);for(const socket of wss.clients)socket.terminate();wss.close();server.closeAllConnections();await new Promise(resolve=>server.close(resolve));rooms.clear();peers.clear();}};
}
if(process.argv[1]&&import.meta.url===pathToFileURL(process.argv[1]).href){const running=await startServer();console.log('VoiceBridge room relay ready');for(const signal of ['SIGINT','SIGTERM'])process.once(signal,()=>void running.stop().then(()=>process.exit()));}
