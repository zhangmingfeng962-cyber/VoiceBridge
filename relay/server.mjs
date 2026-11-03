import http from 'node:http';
import {readFile,writeFile,rename,mkdir,readdir} from 'node:fs/promises';
import path from 'node:path';
import {randomBytes,createHash,timingSafeEqual} from 'node:crypto';
import {pathToFileURL} from 'node:url';
import {WebSocketServer,WebSocket} from 'ws';
import {ScreenRooms,screenConfiguration} from './screen-room.mjs';
import {Outbound} from './outbound.mjs';
import {createAuth} from './auth.mjs';

const hex=n=>randomBytes(n).toString('hex'),hash=s=>createHash('sha256').update(s).digest('hex');
const valid=(value,n)=>typeof value==='string'&&new RegExp('^[a-f0-9]{'+n+'}$').test(value);
const equal=(a,b)=>typeof a==='string'&&typeof b==='string'&&a.length===b.length&&timingSafeEqual(Buffer.from(a),Buffer.from(b));
const headers={'Cache-Control':'no-store','Referrer-Policy':'no-referrer','X-Content-Type-Options':'nosniff','X-Frame-Options':'DENY','Permissions-Policy':'microphone=(self), camera=(), display-capture=(self)','Content-Security-Policy':"default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; connect-src 'self'; media-src 'self' blob: mediastream:; worker-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'"};
const json=(res,status,data)=>{res.writeHead(status,{...headers,'Content-Type':'application/json; charset=utf-8'});res.end(JSON.stringify(data));};
const clean=(value,max)=>typeof value==='string'?value.replace(/[\p{C}]/gu,'').trim().slice(0,max):'';
export async function startServer({port=Number(process.env.PORT||3000),host='0.0.0.0',origin=process.env.PUBLIC_ORIGIN||process.env.RENDER_EXTERNAL_URL,publishKeyHash=process.env.PUBLISH_KEY_HASH,publicRooms=process.env.PUBLIC_ROOM_CREATION==='1',screenConfig=screenConfiguration(),now=Date.now,idleMs=1800000,resumeMs=90000,metricsFile=process.env.DOWNLOAD_METRICS_FILE,authRequired=true,authOptions={}}={}){
  if(!valid(publishKeyHash,64))throw new Error('Configure PUBLISH_KEY_HASH. Never publish the sender key.');
  const rooms=new Map(),peers=new Map(),creationAttempts=new Map(),tickets=new Map(),joinAttempts=new Map();let stopped=false,audioBytes=0,lastMetric=Date.now();
  const assets=new Map();for(const name of await readdir(new URL('./web/',import.meta.url)))if(/^[a-z0-9.-]+\.(html|js|css|ico|svg|png|webmanifest)$/.test(name))assets.set('/'+name,await readFile(new URL('./web/'+name,import.meta.url)));
  const send=(socket,value,binary=false)=>{if(socket?.readyState!==WebSocket.OPEN)return;socket.outbound??=new Outbound(socket,now);socket.outbound.push(value,binary);};
  const screens=new ScreenRooms(send,screenConfig);
  const roster=room=>({type:'members',members:[...peers.values()].filter(p=>p.room===room).map(p=>({id:p.id,name:p.name,userId:p.userId,avatarUrl:p.avatarUrl||null,shortId:p.userId?.slice(0,6),desktop:p.role==='desktop',connected:p.socket?.readyState===WebSocket.OPEN,microphone:p.microphone,music:p.music,source:p.music?p.source:''}))});
  const describe=room=>({id:room.id,name:room.name,public:room.public,ownerId:room.host,ownerName:peers.get(room.host)?.name||'',count:[...peers.values()].filter(p=>p.room===room.id&&p.socket?.readyState===WebSocket.OPEN).length,capacity:8});
  const announce=id=>{const data=roster(id),room=rooms.get(id);for(const p of peers.values())if(p.room===id){send(p.socket,data);if(room)send(p.socket,{type:'room',...describe(room)});if(p.controller){send(p.controller,data);if(room)send(p.controller,{type:'room',...describe(room)});}}screens.notify(id);};
  const removeRoom=id=>{rooms.delete(id);screens.dropRoom(id);for(const p of [...peers.values()])if(p.room===id){p.controller?.close(4004,'Room closed');p.socket.close(4004,'Room closed');peers.delete(p.id);}};
  const roomOccupied=id=>[...peers.values()].some(p=>p.room===id&&(p.socket?.readyState===WebSocket.OPEN||p.controller?.readyState===WebSocket.OPEN));
  const liveRoom=id=>{const room=rooms.get(id);if(!room)return null;
    if(roomOccupied(id)){room.started=true;room.emptySince=null;}
    else if(room.started&&room.emptySince===null)room.emptySince=now();
    if(!room.started&&room.expires<now()||room.started&&room.emptySince!==null&&now()-room.emptySince>=idleMs){removeRoom(id);return null;}return room;
  };
  const dropPeer=(peer,code=1000)=>{if(peers.get(peer.id)!==peer)return;peers.delete(peer.id);screens.detach(peer.id);peer.controller?.close(4004,'Desktop left');peer.socket?.close(code,code===4011?'Removed by owner':'Left room');const room=rooms.get(peer.room);if(room?.host===peer.id){room.host=[...peers.values()].find(p=>p.room===peer.room&&p.socket?.readyState===WebSocket.OPEN)?.id||null;room.ownerAccountId=peers.get(room.host)?.userId||null;}liveRoom(peer.room);announce(peer.room);};
  const auth=authRequired?await createAuth({...authOptions,authorizeAttachment:(identity,body)=>{const member=peers.get(body.memberId);return member?.room===body.roomId&&member?.userId===identity.userId&&member.role==='desktop';},onChange:result=>{
    const affected=new Set();for(const peer of [...peers.values()]){
      if(result.sessionHash&&peer.sessionHash===result.sessionHash||result.revokedUserId===peer.userId&&peer.sessionHash!==result.keepSessionHash){dropPeer(peer,4003);continue;}
      if(result.user?.id===peer.userId){peer.name=result.user.nickname;peer.avatarUrl=result.user.avatarUrl;affected.add(peer.room);}
    }for(const id of affected)announce(id);
  }}):null;
  const readBody=async req=>{let size=0;const pieces=[];for await(const chunk of req){size+=chunk.length;if(size>2048)throw new Error('Body too large');pieces.push(chunk);}return Buffer.concat(pieces).toString();};
  const server=http.createServer(async(req,res)=>{
    try{
      const url=new URL(req.url,origin||`http://127.0.0.1:${server.address().port}`);
      if(req.headers.origin&&req.headers.origin!==url.origin){json(res,403,{error:'Origin rejected'});return;}
      if(auth&&await auth.route(req,res,url,json))return;
      const identity=auth&&url.pathname.startsWith('/api/')?await auth.authenticate(req):null;
      if(auth&&req.method!=='GET'&&(url.pathname.startsWith('/api/rooms')||url.pathname.startsWith('/api/lobby/'))&&!identity){json(res,401,{error:'请登录 VoiceBridge 0.13.0 后加入房间',code:'login_required'});return;}
      if(req.method==='GET'&&url.pathname==='/api/lobby/rooms'){for(const id of [...rooms.keys()])liveRoom(id);json(res,200,{rooms:[...rooms.values()].filter(r=>r.public&&roomOccupied(r.id)).map(describe)});return;}
      const roomStatus=url.pathname.match(/^\/api\/rooms\/([a-f0-9]{32})\/status$/);
      if(req.method==='GET'&&roomStatus){if(auth&&!identity){json(res,401,{error:'请先登录'});return;}const exists=!!liveRoom(roomStatus[1]);json(res,exists?200:404,{exists});return;}
      const publicJoin=url.pathname.match(/^\/api\/lobby\/rooms\/([a-f0-9]{32})\/join$/);
      if(req.method==='POST'&&publicJoin){
        const address=req.socket.remoteAddress||'',previous=joinAttempts.get(address)||{until:now()+60000,count:0};
        if(previous.until<now()){previous.until=now()+60000;previous.count=0;}if(++previous.count>30){json(res,429,{error:'Please wait before joining again'});return;}joinAttempts.set(address,previous);
        const room=liveRoom(publicJoin[1]);if(!room?.public||!roomOccupied(room.id)){json(res,404,{error:'Public room unavailable'});return;}
        if([...peers.values()].filter(p=>p.room===room.id).length>=8){json(res,409,{error:'Room full'});return;}
        if(tickets.size>=512){json(res,429,{error:'Please retry shortly'});return;}const token=hex(32);tickets.set(hash(token),{room:room.id,userId:identity?.userId,until:now()+30000});json(res,200,{id:room.id,name:room.name,token,key:room.publicKey,origin:url.origin});return;
      }
      if(req.method==='GET'&&url.pathname==='/health'){json(res,200,{service:'VoiceBridge encrypted music relay',protocol:3,version:3,release:'0.13.0',authRequired,passwordLogin:true,publicRooms,rename:true,nativeControls:true,lobby:true,resume:true,resumeMs,idleRoomMs:idleMs,roomTimeLimit:null,ready:true,voiceRooms:true,screenShare:1,screenRelay:!!screenConfig.turn.length,codec:'opus',sampleRate:48000,musicBitrate:256000,frameMs:20});return;}
      if(req.method==='GET'&&(['/','/login','/account'].includes(url.pathname)||/^\/rooms?\/[a-f0-9]{32}$/.test(url.pathname))){res.writeHead(200,{...headers,'Content-Type':'text/html; charset=utf-8'});res.end(assets.get(url.pathname==='/'?'/home.html':url.pathname==='/login'?'/login.html':url.pathname==='/account'?'/account.html':'/room.html'));return;}
      if(req.method==='GET'&&assets.has(url.pathname)&&!url.pathname.endsWith('.html')){const extension=url.pathname.split('.').pop(),types={ico:'image/x-icon',png:'image/png',svg:'image/svg+xml',webmanifest:'application/manifest+json',css:'text/css; charset=utf-8',js:'text/javascript; charset=utf-8'};res.writeHead(200,{...headers,'Content-Type':types[extension]||'application/octet-stream'});res.end(assets.get(url.pathname));return;}
      if(req.method==='POST'&&url.pathname==='/api/rooms'){
        const token=(req.headers.authorization||'').replace(/^Bearer /,''),authorized=valid(token,64)&&equal(hash(token),publishKeyHash);
        if(!authorized&&!publicRooms&&!identity){json(res,403,{error:'Public room creation is disabled'});return;}
        if(!authorized){
          const address=req.socket.remoteAddress||'unknown',now=Date.now();for(const [key,value] of creationAttempts)if(value.until<=now)creationAttempts.delete(key);
          const attempt=creationAttempts.get(address)||{count:0,until:now+60000};if(attempt.count>=4||creationAttempts.size>1024){res.setHeader('Retry-After','60');json(res,429,{error:'Please wait before creating another room'});return;}attempt.count++;creationAttempts.set(address,attempt);
        }
        const body=JSON.parse(await readBody(req)||'{}');for(const id of [...rooms.keys()])liveRoom(id);if(rooms.size>=4){json(res,429,{error:'Too many active rooms'});return;}
        if(body.public===true&&!valid(body.publicKey,64)){json(res,400,{error:'Public room key required'});return;}
        const id=hex(16),writeToken=hex(32),listenToken=hex(32);rooms.set(id,{id,name:clean(body.name,32)||'音乐房间',public:body.public===true,publicKey:body.public===true?body.publicKey:null,writeHash:hash(writeToken),listenHash:hash(listenToken),created:now(),expires:now()+(authorized?120000:30000),started:false,emptySince:null,host:null,ownerAccountId:identity?.userId,banned:new Set()});json(res,201,{id,writeToken,listenToken,protocol:3});return;
      }
      const match=url.pathname.match(/^\/api\/rooms\/([a-f0-9]{32})$/);
      if(match&&req.method==='DELETE'){
        const room=liveRoom(match[1]),token=(req.headers.authorization||'').replace(/^Bearer /,'');if(!room){json(res,404,{error:'Room closed'});return;}
        if(!valid(token,64)||!equal(hash(token),room.writeHash)){json(res,403,{error:'Owner credential invalid'});return;}
        if([...peers.values()].some(p=>p.room===room.id)){json(res,409,{error:'Members are still in this room; leaving does not close it'});return;}
        removeRoom(room.id);json(res,200,{stopped:true});return;
      }
      json(res,/^\/api\/rooms\//.test(url.pathname)?426:404,{error:'Please use VoiceBridge 0.9 or newer'});
    }catch{if(!res.headersSent)json(res,400,{error:'Invalid room request'});else res.end();}
  });
  const wss=new WebSocketServer({noServer:true,maxPayload:96000,perMessageDeflate:false,handleProtocols:protocols=>protocols.has('voicebridge')?'voicebridge':false});
  server.on('upgrade',async(req,socket,head)=>{
    try{
    const match=req.url?.match(/^\/api\/rooms\/([a-f0-9]{32})\/voice$/),expected=origin||`http://127.0.0.1:${server.address().port}`;
    if(stopped||!match||req.headers.origin!==expected||wss.clients.size>=80||!liveRoom(match[1])){socket.end('HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n');return;}
    let identity=auth?await auth.authenticate(req):null;
    if(auth&&!identity){const protocols=String(req.headers['sec-websocket-protocol']||'').split(',').map(v=>v.trim());const ticket=protocols.find(v=>/^vb-ticket\.[a-f0-9]{64}$/.test(v))?.slice(10);identity=auth.consumeAttachment(ticket);}
    if(auth&&!identity){socket.end('HTTP/1.1 401 Unauthorized\r\nConnection: close\r\n\r\n');return;}
    wss.handleUpgrade(req,socket,head,ws=>{ws.identity=identity;wss.emit('connection',ws,match[1]);});
    }catch{socket.end('HTTP/1.1 503 Service Unavailable\r\nConnection: close\r\n\r\n');}
  });
  wss.on('connection',(socket,roomId)=>{
    let peer=null,controllerOf=null,connectionId=null,joined=false,lastSeen=now(),frames=[0,0],controls=0,start=Date.now(),lastControl=-1;
    const deadline=setTimeout(()=>socket.close(4003,'Join required'),10000);deadline.unref();socket.on('error',()=>{});socket.on('pong',()=>{lastSeen=now();});
    socket.on('close',(code)=>{
      clearTimeout(deadline);
      if(controllerOf)screens.detach(controllerOf.id,socket);
      if(controllerOf&&controllerOf.controller===socket){controllerOf.controller=null;if(!controllerOf.nativeControls)controllerOf.microphone=false;send(controllerOf.socket,{type:'controllerDetached'});announce(roomId);}
      if(peer&&peer.socket===socket&&peers.get(peer.id)===peer){
        if(!peer.resumeHash||socket.explicitLeave||code===4003){dropPeer(peer);return;}
        peer.disconnectedAt=now();peer.microphone=false;peer.music=false;
        // Keep the paired page and an existing independent video connection alive.
        if(peer.role==='browser')screens.suspend(peer.id,socket);
        send(peer.controller,{type:'desktopConnection',connected:false});liveRoom(roomId);announce(roomId);
      }
    });
    socket.on('message',async(data,binary)=>{
      try{
        if(Date.now()-start>=1000){start=Date.now();controls=0;frames=[0,0];}
        if(!joined){
          if(binary)throw new Error('Join required');const input=JSON.parse(data.toString()),room=liveRoom(roomId);if(!room||input.type!=='join'||!([3,4].includes(input.version))||auth&&input.version!==4)throw new Error('Upgrade required');
          if(input.role==='controller'){
            const target=peers.get(input.memberId);if(auth&&(target?.userId!==socket.identity?.userId||socket.identity.scope==='controller'&&(socket.identity.roomId!==roomId||socket.identity.memberId!==input.memberId)))throw Error('Controller identity invalid');if(!target||target.room!==roomId||target.role!=='desktop'||!valid(input.controlToken,64)||!equal(input.controlToken,target.controlToken))throw new Error('Control capability invalid');
            target.controller?.close(4001,'Replaced by another control page');controllerOf=target;target.controller=socket;connectionId=hex(16);target.connectionId=connectionId;target.lastState=-1;target.pendingControl=null;if(target.hadController&&!target.nativeControls)target.microphone=false;target.hadController=true;
            joined=true;clearTimeout(deadline);send(socket,{type:'welcome',role:'controller',id:target.id,connectionId});screens.attach(target,socket);send(target.socket,{type:'controllerAttached',connectionId});send(socket,roster(roomId));announce(roomId);return;
          }
          if(auth&&(socket.identity.scope==='controller'||room.banned.has(socket.identity.userId))){socket.close(4011,'Not admitted');return;}
          if(!['desktop','browser'].includes(input.role))throw new Error('Invalid role');
          if(input.resume){
            const previous=peers.get(input.resume.memberId);
            if(!previous||auth&&previous.userId!==socket.identity.userId||previous.room!==roomId||previous.role!==input.role||!valid(input.resume.token,64)||!equal(hash(input.resume.token),previous.resumeHash)||previous.disconnectedAt!==null&&now()-previous.disconnectedAt>resumeMs){socket.close(4010,'Resume expired');return;}
            const old=previous.socket;peer=previous;peer.socket=socket;peer.disconnectedAt=null;peer.sessionHash=socket.identity?.sessionHash;old?.close(4001,'Connection replaced');joined=true;clearTimeout(deadline);room.emptySince=null;
            send(socket,{type:'welcome',role:peer.role,id:peer.id,resumed:true,resumeMs,controlToken:peer.controlToken,controllerPresent:!!peer.controller,connectionId:peer.connectionId,pendingControl:peer.pendingControl});
            if(peer.role==='browser')screens.attach(peer,socket,true);
            send(peer.controller,{type:'desktopConnection',connected:true});announce(roomId);return;
          }
          const admission=valid(input.token,64)?tickets.get(hash(input.token)):null;
          if(!valid(input.token,64)||!equal(hash(input.token),room.listenHash)&&!(room.public&&admission?.room===roomId&&admission.until>=now()&&(!auth||admission.userId===socket.identity.userId)))throw new Error('Invitation invalid');
          if(admission)tickets.delete(hash(input.token));
          if([...peers.values()].filter(p=>p.room===roomId).length>=8){socket.close(4009,'Room full');return;}
          const owner=valid(input.hostToken,64)&&equal(hash(input.hostToken),room.writeHash)&&(!auth||room.ownerAccountId===socket.identity.userId),resumeToken=input.canResume?hex(32):null;
          peer={id:hex(16),name:socket.identity?.user.nickname||clean(input.name,20)||'朋友',userId:socket.identity?.userId,avatarUrl:socket.identity?.user.avatarUrl,sessionHash:socket.identity?.sessionHash,room:roomId,role:input.role,nativeControls:input.role==='desktop'&&input.nativeControls===true,socket,controller:null,controlToken:input.role==='desktop'?hex(32):null,connectionId:null,lastState:-1,hadController:false,microphone:false,music:false,source:'',lastSequences:[-1,-1],resumeHash:resumeToken?hash(resumeToken):null,disconnectedAt:null,pendingControl:null};
          peers.set(peer.id,peer);if(owner)room.host=peer.id;room.started=true;room.emptySince=null;joined=true;clearTimeout(deadline);send(socket,{type:'welcome',role:peer.role,id:peer.id,resumeToken,resumeMs,...(peer.controlToken?{controlToken:peer.controlToken}:{})});if(peer.role==='browser')screens.attach(peer,socket);announce(roomId);return;
        }
        if(!liveRoom(roomId)){socket.close(4004,'Room closed');return;}
        if(binary){
          if(!peer||data.length<38||data.length>1312||data.subarray(0,4).toString()!=='VBA2'||data[4]>1)throw new Error('Audio frame invalid');
          const kind=data[4],sequence=data.readUInt32LE(5);if(++frames[kind]>80||sequence<=peer.lastSequences[kind])throw new Error('Audio rate or replay');peer.lastSequences[kind]=sequence;
          if(kind===0&&!peer.microphone||kind===1&&!peer.music)return; // Safely discard a frame already queued when a user mutes.
          const packet=Buffer.concat([Buffer.from(peer.id,'hex'),data]);for(const target of peers.values())if(target.room===roomId&&target.id!==peer.id){audioBytes+=packet.length;send(target.socket,packet,true);}return;
        }
        const message=JSON.parse(data.toString());
        if(message.type==='ping'){if(++controls>60)throw new Error('Control rate');send(socket,{type:'pong',at:Number.isFinite(message.at)?message.at:null,sequences:peer?.lastSequences});return;}
        if(message.type==='leave'&&peer){socket.explicitLeave=true;dropPeer(peer);return;}
        if(typeof message.type==='string'&&message.type.startsWith('screen:')){screens.handle(controllerOf||peer,socket,message);return;}
        if(++controls>60)throw new Error('Control rate');
        if(message.type==='room:update'||message.type==='member:kick'){
          const target=controllerOf||peer,room=rooms.get(roomId);if(room?.host!==target?.id){send(socket,{type:'room:error',error:'Only the owner can manage this room'});return;}
          if(message.type==='member:kick'){const victim=peers.get(message.memberId);if(victim&&victim.room===roomId&&victim!==target){if(victim.userId)room.banned.add(victim.userId);dropPeer(victim,4011);}return;}
          if(message.name!==undefined){const name=clean(message.name,32);if(!name){send(socket,{type:'room:error',error:'Room name required'});return;}room.name=name;}
          if(typeof message.public==='boolean'){if(message.public&&!room.public&&!valid(message.publicKey,64)){send(socket,{type:'room:error',error:'Public room key required'});return;}
            if(!message.public&&room.public){const token=hex(32);room.listenHash=hash(token);for(const member of peers.values())if(member.room===roomId){send(member.socket,{type:'invitation',token});send(member.controller,{type:'invitation',token});}}
            room.public=message.public;if(message.public&&valid(message.publicKey,64))room.publicKey=message.publicKey;}
          announce(roomId);return;
        }
        if(message.type==='rename'){
          const target=controllerOf||peer,name=clean(message.name,20);if(!target||!name)throw new Error('Nickname required');if(auth){const result=await auth.rpc('renameUser',{userId:target.userId,nickname:name});for(const other of peers.values())if(other.userId===target.userId){other.name=result.user.nickname;announce(other.room);}}else{target.name=name;announce(roomId);}return;
        }
        if(controllerOf){
          if(message.type!=='control'||!Number.isSafeInteger(message.sequence)||message.sequence<=lastControl||typeof message.iv!=='string'||message.iv.length!==16||typeof message.data!=='string'||message.data.length>64000)throw new Error('Control packet invalid');
          lastControl=message.sequence;controllerOf.pendingControl={...message,connectionId};send(controllerOf.socket,controllerOf.pendingControl);return;
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
    socket.roomHeartbeat=()=>{if(now()-lastSeen>=45000){socket.terminate();return;}if(socket.readyState===WebSocket.OPEN)socket.ping();};
  });
  const maintenance=({heartbeat=true}={})=>{
    for(const [key,ticket] of tickets)if(ticket.until<now())tickets.delete(key);
    for(const [key,attempt] of joinAttempts)if(attempt.until<now())joinAttempts.delete(key);
    for(const peer of [...peers.values()])if(peer.disconnectedAt!==null&&now()-peer.disconnectedAt>=resumeMs)dropPeer(peer);
    for(const id of [...rooms.keys()])liveRoom(id);
    if(heartbeat)for(const socket of wss.clients)socket.roomHeartbeat?.();
  };
  const cleanup=setInterval(maintenance,15000);cleanup.unref();
  const flush=setInterval(()=>{for(const socket of wss.clients)socket.outbound?.flush();},10);flush.unref();
  let metricsBusy=false;
  const metrics=setInterval(async()=>{if(!metricsFile||metricsBusy)return;metricsBusy=true;try{const elapsed=Math.max(1,Date.now()-lastMetric),bitsPerSecond=audioBytes*8000/elapsed;audioBytes=0;lastMetric=Date.now();await mkdir(path.dirname(metricsFile),{recursive:true});const temporary=metricsFile+'.'+process.pid+'.tmp';await writeFile(temporary,JSON.stringify({at:Date.now(),audioBits:Math.ceil(bitsPerSecond*1.25),screenBits:screens.shares.size?screenConfig.budget:0,active:peers.size>0}),{mode:0o640});await rename(temporary,metricsFile);}catch{}finally{metricsBusy=false;}},1000);metrics.unref();
  server.headersTimeout=10000;server.requestTimeout=15000;server.maxConnections=128;
  await new Promise((resolve,reject)=>{server.once('error',reject);server.listen(port,host,resolve);});
  return {server,rooms,peers,screens,maintenance,url:`http://127.0.0.1:${server.address().port}`,stop:async()=>{stopped=true;clearInterval(cleanup);clearInterval(flush);clearInterval(metrics);for(const socket of wss.clients)socket.terminate();wss.close();server.closeAllConnections();await new Promise(resolve=>server.close(resolve));await auth?.stop();rooms.clear();peers.clear();screens.clients.clear();screens.shares.clear();creationAttempts.clear();tickets.clear();joinAttempts.clear();}};
}
if(process.argv[1]&&import.meta.url===pathToFileURL(process.argv[1]).href){const running=await startServer();console.log('VoiceBridge room relay ready');for(const signal of ['SIGINT','SIGTERM'])process.once(signal,()=>void running.stop().then(()=>process.exit()));}
