import {RoomCrypto} from './crypto.js';
// The UI never owns PCM delivery, codecs, encryption, or heartbeat timers.
let socket=null,generation=0,closed=false,joinRequest=null,resume=null,cipher=null,selfId='',roster=new Set(),publicRoom=false,attempt=0,retryTimer,heartbeat,lastPong=0,mixer=null;
let attachment=null,pendingReconnect=false;
let networkReady=false,sending=false,receiving=Promise.resolve();const encoders=[null,null],capturePorts=[null,null],queues=[[],[]],sequences=[0,0],lastSequence=new Map(),gains=new Map();
let desired={microphone:false,sharing:false,talkMode:'free',pressed:false,micEpoch:0,shareEpoch:0};
const emit=value=>self.postMessage(value);
const endpoint=()=>location.origin.replace(/^http/,'ws')+location.pathname;
function gate(kind){return networkReady&&(kind?desired.sharing:desired.microphone&&(desired.talkMode==='free'||desired.pressed));}
function codecError(kind){emit({type:'codecError',kind,message:'音频编码已重置，请关闭后重新开启该路声音'});stopCapture(kind);}
function stopCapture(kind){if(kind===0)desired.microphone=false;else desired.sharing=false;const item=encoders[kind];encoders[kind]=null;if(item?.encoder.state!=='closed')item?.encoder.close();capturePorts[kind]?.close();capturePorts[kind]=null;queues[kind]=[];}
function attachCapture(kind,port){
 stopCapture(kind);capturePorts[kind]=port;const channels=kind?2:1,pending=new Map();let stamp=0;
 try{const encoder=new AudioEncoder({output:chunk=>{const epoch=pending.get(chunk.timestamp);pending.delete(chunk.timestamp);if(epoch!==(kind?desired.shareEpoch:desired.micEpoch)||!gate(kind))return;const bytes=new Uint8Array(chunk.byteLength);chunk.copyTo(bytes);queues[kind].push({bytes,epoch,at:performance.now()});if(queues[kind].length>6)queues[kind].shift();void drain();},error:()=>{if(encoders[kind]?.encoder===encoder)codecError(kind);}});encoder.configure({codec:'opus',sampleRate:48000,numberOfChannels:channels,bitrate:kind?256000:48000,opus:{format:'opus',frameDuration:20000}});encoders[kind]={encoder,pending};port.onmessage=({data})=>{if(encoders[kind]?.encoder!==encoder||!gate(kind)||data.epoch!==(kind?desired.shareEpoch:desired.micEpoch)||encoder.encodeQueueSize>4)return;const frame=new AudioData({format:'f32',sampleRate:48000,numberOfFrames:960,numberOfChannels:channels,timestamp:stamp,data:data.pcm});pending.set(stamp,data.epoch);stamp+=20000;encoder.encode(frame);frame.close();};port.start();}catch{codecError(kind);}
}
async function drain(){
 if(sending)return;sending=true;try{while(networkReady&&socket?.readyState===1&&(queues[0].length||queues[1].length)){
  const kind=queues[0].length?0:1,item=queues[kind].shift();if(!gate(kind)||item.epoch!==(kind?desired.shareEpoch:desired.micEpoch)||performance.now()-item.at>120)continue;
  if(socket.bufferedAmount>16384){queues[1]=[];break;}const wire=generation,packet=await cipher.sealAudio(kind,selfId,sequences[kind]++,item.bytes);if(wire===generation&&gate(kind)&&item.epoch===(kind?desired.shareEpoch:desired.micEpoch)&&socket?.readyState===1)socket.send(packet);
 }}catch{emit({type:'notice',message:'音频发送遇到网络波动，正在恢复'});}finally{sending=false;}
}
const decoders=new Map();
function removeMember(id){mixer?.postMessage({type:'remove',id});for(const [key,item] of decoders)if(key.startsWith(id+':')){if(item.state!=='closed')item.close();decoders.delete(key);}}
let pendingDecode=0;
function receiveAudio(data,wire){if(!mixer||!cipher||pendingDecode>=24)return;pendingDecode++;receiving=receiving.then(async()=>{if(wire!==generation)return;const frame=await cipher.openAudio(data);if(wire!==generation||frame.id===selfId||!roster.has(frame.id))return;const key=frame.id+':'+frame.kind;if(frame.sequence<=(lastSequence.get(key)??-1))return;lastSequence.set(key,frame.sequence);let decoder=decoders.get(key);
 if(!decoder){const channels=frame.kind?2:1;decoder=new AudioDecoder({output:audio=>{try{if(!mixer)return;const planes=[];for(let c=0;c<audio.numberOfChannels;c++){const plane=new Float32Array(audio.numberOfFrames);audio.copyTo(plane,{planeIndex:c,format:'f32-planar'});planes.push(plane);}const samples=new Float32Array(audio.numberOfFrames*2);let peak=0;for(let n=0;n<audio.numberOfFrames;n++){samples[n*2]=planes[0][n];samples[n*2+1]=planes[Math.min(1,planes.length-1)][n];peak=Math.max(peak,Math.abs(samples[n*2]),Math.abs(samples[n*2+1]));}mixer.postMessage({type:'packet',key,samples:samples.buffer,arrival:performance.now(),gain:gains.get(key)??1,peak},[samples.buffer]);}finally{audio.close();}},error:()=>{decoders.delete(key);emit({type:'notice',message:'一路接收音频正在自动恢复'});}});decoder.configure({codec:'opus',sampleRate:48000,numberOfChannels:channels});decoders.set(key,decoder);}
 if(decoder.decodeQueueSize<8)decoder.decode(new EncodedAudioChunk({type:'key',timestamp:frame.sequence*20000,duration:20000,data:frame.opus}));
 }).catch(()=>emit({type:'notice',message:'收到无法解密的音频，请核对房间邀请'})).finally(()=>pendingDecode--);}
function flags(){if(socket?.readyState===1&&networkReady&&joinRequest?.role!=='controller')socket.send(JSON.stringify({type:'flags',microphone:desired.microphone,music:desired.sharing,source:joinRequest?.source||'浏览器标签页'}));}
function clearNetwork(){networkReady=false;desired.pressed=false;queues[0]=[];queues[1]=[];clearInterval(heartbeat);}
function terminal(code){return [4001,4003,4004,4009,4011].includes(code);}
function lost(ws,code=1006){
 if(ws!==socket||closed)return;clearNetwork();socket=null;generation++;try{ws.close(4008,'Recovering');}catch{}
 if(terminal(code)){closed=true;emit({type:'close',code});return;}
 if(code===4010){resume=null;selfId='';lastSequence.clear();}
 emit({type:'reconnecting',code});const delay=Math.min(5000,500*2**Math.min(attempt++,4))*(.8+Math.random()*.4);clearTimeout(retryTimer);retryTimer=setTimeout(()=>void connect(true),delay);
}
async function connect(reconnecting=false){
 if(closed)return;try{
  if(reconnecting&&!joinRequest.requireAttachment){const status=await fetch('/api/rooms/'+joinRequest.roomId+'/status',{cache:'no-store'});if(status.status===401||status.status===404){closed=true;emit({type:'close',code:status.status===401?4003:4004});return;}if(!status.ok)throw Error('Room status unavailable');}
  if(joinRequest.requireAttachment&&!attachment){pendingReconnect=reconnecting;emit({type:'needAttachment'});return;}
  if(reconnecting&&!resume&&publicRoom&&joinRequest?.role!=='controller'){const response=await fetch('/api/lobby/rooms/'+joinRequest.roomId+'/join',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'});if(!response.ok){if(response.status===401||response.status===404){closed=true;emit({type:'close',code:response.status===401?4003:4004});return;}throw Error('Admission unavailable');}const entry=await response.json();joinRequest.token=entry.token;cipher=new RoomCrypto(joinRequest.roomId,entry.key);}
  const protocols=attachment?['voicebridge','vb-ticket.'+attachment]:undefined;attachment=null;const wire=++generation,ws=protocols?new WebSocket(joinRequest.url,protocols):new WebSocket(joinRequest.url);socket=ws;ws.binaryType='arraybuffer';lastPong=Date.now();
  ws.onopen=()=>{if(wire!==generation||closed){ws.close();return;}if(reconnecting||joinRequest.type==='join'){const request={...joinRequest};delete request.url;delete request.roomId;delete request.requireAttachment;delete request.resume;if(resume)request.resume=resume;ws.send(JSON.stringify(request));}if(!reconnecting)emit({type:'open'});heartbeat=setInterval(()=>{if(ws!==socket)return;if(Date.now()-lastPong>20000){lost(ws);return;}if(ws.readyState===1)ws.send(JSON.stringify({type:'ping',at:Date.now()}));},5000);};
  ws.onmessage=event=>{if(wire!==generation||closed)return;if(event.data instanceof ArrayBuffer){receiveAudio(event.data,wire);return;}let message;try{message=JSON.parse(event.data);}catch{return;}
   if(message.type==='pong'){lastPong=Date.now();return;}
   if(message.type==='welcome'){selfId=message.id;if(message.resumeToken)resume={memberId:selfId,token:message.resumeToken};networkReady=true;attempt=0;flags();emit({type:'resume',value:resume});}
   if(message.type==='room')publicRoom=message.public===true;
   if(message.type==='invitation')joinRequest.token=message.token;
   if(message.type==='members'){const next=new Set(message.members.map(m=>m.id));for(const id of roster)if(!next.has(id))removeMember(id);roster=next;}
   emit({type:'message',data:event.data});
  };ws.onclose=event=>lost(ws,event.code);ws.onerror=()=>{if(ws===socket)emit({type:'error'});};
  const started=Date.now();setTimeout(()=>{if(ws===socket&&!networkReady&&Date.now()-started>=14500)lost(ws);},15000);
 }catch{if(!closed){emit({type:'reconnecting',code:1006});clearTimeout(retryTimer);retryTimer=setTimeout(()=>void connect(true),5000);}}
}
self.onmessage=({data:m})=>{
 if(m.type==='connect'){closed=false;joinRequest={url:m.url,roomId:m.roomId,role:m.role,requireAttachment:m.requireAttachment,...(m.join||{})};resume=m.join?.resume||null;delete joinRequest.resume;cipher=new RoomCrypto(m.roomId,m.key);void connect();}
 if(m.type==='attachment'){attachment=m.ticket;if(joinRequest&&!socket)void connect(pendingReconnect);}
 if(m.type==='send'){let value;try{value=JSON.parse(m.data);}catch{return;}if(value.type==='join'){joinRequest={...joinRequest,...value};if(value.resume)resume=value.resume;delete joinRequest.resume;}if(value.type==='flags'){desired.microphone=value.microphone;desired.sharing=value.music;joinRequest.source=value.source;}if(value.type==='leave'){closed=true;clearTimeout(retryTimer);clearNetwork();}if(socket?.readyState===1)socket.send(m.data);}
 if(m.type==='desired'){desired={...desired,...m.value};if(!gate(0))queues[0]=[];if(!gate(1))queues[1]=[];}
 if(m.type==='capture')attachCapture(m.kind,m.port);
 if(m.type==='stopCapture')stopCapture(m.kind);
 if(m.type==='mixer'){mixer=m.port;mixer.start();}
 if(m.type==='gain')gains.set(m.id+':'+m.kind,m.value/100);
 if(m.type==='remove')removeMember(m.id);
 if(m.type==='close'){closed=true;clearTimeout(retryTimer);clearNetwork();try{socket?.close(m.code||1000,m.reason||'Page closed');}catch{}socket=null;for(const kind of [0,1])stopCapture(kind);for(const decoder of decoders.values())if(decoder.state!=='closed')decoder.close();decoders.clear();mixer?.close();mixer=null;}
};
