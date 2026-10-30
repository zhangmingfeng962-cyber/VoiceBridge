import {RoomCrypto} from './crypto.js';
import {BrowserAudio} from './audio.js';
import {membershipCues} from './room-events.js';
import {ScreenView} from './screen-view.js';
import {readPreferences,savePreferences,cleanNickname} from './preferences.js';
const el=id=>document.getElementById(id),room=location.pathname.match(/^\/room\/([a-f0-9]{32})$/)?.[1],parts=location.hash.slice(1).split('.');
const token=parts[0],key=parts[1],paired=parts[2]==='desktop',nativeId=parts[3],controlToken=parts[4];
const cipher=room&&/^[a-f0-9]{64}$/.test(key||'')?new RoomCrypto(room,key):null;
const preferences=readPreferences();
const defaults=()=>({microphone:false,noiseSuppression:preferences.noiseSuppression,microphoneId:'',outputId:'',microphoneGain:100,speakerGain:parts.includes('silent')?0:100,talkMode:'free',talkKey:86,sharing:false,sourceId:'',musicGain:100,autoBuffer:true,memberVoice:{},memberMusic:{}});
let options=defaults(),socket,audio,selfId,connectionId,joined=false,epoch=0,revision=0,pendingRevision=0,controlSequence=0,stateSequence=-1,sendSequence=[0,0],sending=Promise.resolve(),receiving=Promise.resolve(),controlSending=Promise.resolve(),pendingFrames=0,bind=false,updateTimer,roster=[],seenMembers=false,micRequest=0,nativeReady=false;
const cards=new Map(),lastReceived=new Map();
const channel=window.BroadcastChannel&&room?new BroadcastChannel('voicebridge-room:'+room):null;
const text=message=>{el('status').textContent=message;};
const keyName=code=>code>=65&&code<=90?String.fromCharCode(code):code>=48&&code<=57?String.fromCharCode(code):code>=112&&code<=135?'F'+(code-111):({32:'空格',16:'Shift',17:'Ctrl',18:'Alt',9:'Tab'})[code]||'键 '+code;
const simpleInvite=()=>location.origin+'/room/'+room+'#'+token+'.'+key;
const controlIds=['microphone','noiseSuppression','talkMode','bindKey','micDevice','outputDevice','micGain','micDown','micUp','speakerGain','speakerDown','speakerUp','sourceSelect','share','shareTab','musicGain','autoBuffer'];
let memberResume=null,reconnectTimer,heartbeat,wireEpoch=0,recoveryStarted=0,retryCount=0,restoreController=false;
const connectionState=(message,warn=false)=>{el('connectionBadge').textContent=message;el('connectionBadge').classList.toggle('warn',warn);};
let lastUnderruns=0,audioPressureUntil=0;
const screenView=new ScreenView({
  send:message=>{if(joined&&socket?.readyState===WebSocket.OPEN&&socket.bufferedAmount<32768)socket.send(JSON.stringify(message));},
  sound:()=>({sharing:options.sharing,sourceId:options.sourceId,volume:options.musicGain,program:paired,ready:!paired||nativeReady,listening:options.memberMusic}),
  setSound:async value=>{if(!joined)return;if(paired){if(value&&!options.sourceId)throw Error('请先选择一个本机程序音源');options.sharing=value;changed();}else{if(value){await audio.shareTab();options.sharing=!!audio.capture[1];}else{audio.stopCapture(1);options.sharing=false;}changed();}},
  setVolume:value=>{options.musicGain=value;changed();},
  setSource:value=>{options.sourceId=value;changed();},
  setListening:(id,value)=>{options.memberMusic[id]=value;changed();},
  audioPressure:()=>performance.now()<audioPressureUntil||pendingFrames>4||(socket?.bufferedAmount||0)>16384
});
function enabled(value){for(const id of controlIds)el(id).disabled=!value;el('saveNickname').disabled=false;el('join').disabled=value;el('join').hidden=value;el('leave').disabled=!value;}
function refreshControls(){
  el('microphone').textContent=options.microphone?'关闭麦克风':'开启麦克风';el('microphone').setAttribute('aria-pressed',String(options.microphone));
  el('noiseSuppression').textContent=options.noiseSuppression?'麦克风降噪：开':'麦克风降噪：关';el('noiseSuppression').setAttribute('aria-pressed',String(options.noiseSuppression));
  el('share').textContent=options.sharing?'停止共享音乐':'开始共享音乐';el('share').setAttribute('aria-pressed',String(options.sharing));
  for(const [id,name] of [['micGain','microphoneGain'],['speakerGain','speakerGain'],['musicGain','musicGain']]){if(document.activeElement!==el(id))el(id).value=options[name];el(id+'Value').textContent=Math.round(options[name])+'%';}
  el('talkMode').value=options.talkMode;el('bindKey').textContent=bind?'按下要设置的按键…':'说话键：'+keyName(options.talkKey);el('autoBuffer').checked=options.autoBuffer;
  el('pttHint').textContent=options.talkMode==='ptt'?(paired?'客户端运行时，游戏后台也可按住 '+keyName(options.talkKey)+' 说话。':'网页模式需保持页面焦点，按住 '+keyName(options.talkKey)+' 说话；切走页面会停止发送。'):'自由发言：麦克风开启后持续传送人声，讲话不会压低音乐。';
  el('share').hidden=!paired;el('shareTab').hidden=paired;el('outputWrap').hidden=!paired;el('clientHelp').hidden=paired;
  if(!paired){el('sourceSelect').disabled=true;el('sourceHint').textContent='网页可选择其他浏览器标签页的音频；需要自动识别微信等桌面程序时，请使用 Windows 客户端加入本房间。';}
  screenView.syncSound();
}
function choices(id,items,selected,empty){const select=el(id),signature=JSON.stringify(items);if(select.dataset.signature!==signature){select.replaceChildren();if(empty){const option=document.createElement('option');option.value='';option.textContent=empty;select.append(option);}for(const item of items){const option=document.createElement('option');option.value=item.id;option.textContent=item.name+(item.active?' · 正在发声':'');select.append(option);}select.dataset.signature=signature;}select.value=selected;if(select.selectedIndex<0)select.selectedIndex=0;}
function applyGains(){audio?.configure(options);for(const member of roster){if(member.id===selfId)continue;const card=cards.get(member.id);if(!card)continue;for(const [kind,field] of [[0,'memberVoice'],[1,'memberMusic']]){const value=options[field]?.[member.id]??100,input=card.querySelector(`[data-kind="${kind}"]`),out=card.querySelector(`[data-value="${kind}"]`);if(document.activeElement!==input)input.value=value;out.textContent=Math.round(value)+'%';}}}
function members(){
  const mine=roster.find(m=>m.id===selfId);if(mine){if(document.activeElement!==el('nickname'))el('nickname').value=mine.name;savePreferences({nickname:mine.name});}
  const present=new Set(roster.map(m=>m.id));for(const [id,card] of cards)if(!present.has(id)){card.remove();cards.delete(id);audio?.remove(id);}
  for(const member of roster){
    let card=cards.get(member.id);if(!card){card=document.createElement('section');card.className='member';const header=document.createElement('div');header.className='memberHead';const name=document.createElement('span');name.className='memberName';const mute=document.createElement('button');mute.className='mini';mute.textContent='静音此人';mute.addEventListener('click',()=>{options.memberVoice[member.id]=(options.memberVoice[member.id]??100)>0?0:100;applyGains();changed();});header.append(name,mute);const detail=document.createElement('p');detail.className='memberStatus';card.append(header,detail);
      for(const [kind,title] of [[0,'此人人声'],[1,'此人音乐']]){const label=document.createElement('label');label.textContent=title;const output=document.createElement('output');output.dataset.value=kind;label.append(output);const input=document.createElement('input');input.type='range';input.min=0;input.max=kind===0?200:100;input.value=100;input.dataset.kind=kind;input.setAttribute('aria-label',title+'音量');input.addEventListener('input',()=>{options[kind===0?'memberVoice':'memberMusic'][member.id]=Number(input.value);output.textContent=input.value+'%';audio?.setGain(member.id,kind,Number(input.value));changed();});card.append(label,input);}el('members').append(card);cards.set(member.id,card);
    }
    card.querySelector('.memberName').textContent=member.name+(member.id===selfId?'（你）':'');card.querySelector('.memberStatus').textContent=member.connected===false?'网络波动 · 正在重连':(member.microphone?'麦克风开启':'麦克风关闭')+(member.music?' · 正在共享 '+(member.source||'音乐'):'');
    const self=member.id===selfId;for(const child of card.querySelectorAll('label,input,button'))child.hidden=self;
    card.classList.toggle('off',!member.microphone&&!member.music);
  }
  el('onlineCount').textContent=roster.length+' / 8 人';applyGains();
}
function network(streams,decoded){const music=streams.filter(s=>s.id.endsWith(':1')),target=Math.max(0,...(music.length?music:streams).map(s=>s.targetMs));const under=streams.reduce((n,s)=>n+s.underruns,0);if(under>lastUnderruns)audioPressureUntil=performance.now()+8000;lastUnderruns=under;el('network').textContent=streams.length?(options.autoBuffer?'自动缓冲':'当前缓冲')+' · '+Math.round(target)+' ms'+(under?' · 曾等待补充缓冲 '+under+' 次':' · 连续接收中'):'等待其他成员发声或共享音乐';el('metrics').textContent='48 kHz · 立体声音乐 · Opus 20 ms 小帧'+(decoded!==undefined?' · 已解码 '+decoded+' 帧':' · 客户端独立播放');}
function changed(){refreshControls();applyGains();if(!joined)return;if(paired){pendingRevision=++revision;clearTimeout(updateTimer);updateTimer=setTimeout(()=>{const message={action:'configure',revision:pendingRevision,options:structuredClone(options)},current=epoch,wire=wireEpoch,connection=connectionId;controlSending=controlSending.then(async()=>{if(current!==epoch||wire!==wireEpoch||socket?.readyState!==WebSocket.OPEN)return;const sealed=await cipher.sealControl(selfId,connection,controlSequence++,message);if(current===epoch&&wire===wireEpoch&&socket?.readyState===WebSocket.OPEN)socket.send(JSON.stringify(sealed));}).catch(()=>text('控制发送失败，请重新打开客户端的控制页'));},70);}else{audio?.configure(options);sendFlags();}}
function sendFlags(){if(socket?.readyState===WebSocket.OPEN&&!paired)socket.send(JSON.stringify({type:'flags',microphone:options.microphone,music:options.sharing,source:options.sharing?'浏览器标签页':''}));}
function sendAudio(kind,opus,captureEpoch){
  if(!joined||paired||pendingFrames>=8||socket?.readyState!==WebSocket.OPEN||socket.bufferedAmount>16384)return;const current=epoch,wire=wireEpoch,created=performance.now();pendingFrames++;
  sending=sending.then(async()=>{if(current!==epoch||wire!==wireEpoch||performance.now()-created>120||!audio)return;if(kind===0&&(!options.microphone||captureEpoch!==audio.micEpoch)||kind===1&&(!options.sharing||captureEpoch!==audio.shareEpoch))return;const packet=await cipher.sealAudio(kind,selfId,sendSequence[kind]++,opus);if(current===epoch&&wire===wireEpoch&&socket?.readyState===WebSocket.OPEN&&(kind===1?captureEpoch===audio?.shareEpoch:captureEpoch===audio?.micEpoch))socket.send(packet);}).catch(()=>text('音频发送遇到网络波动，正在等待恢复')).finally(()=>pendingFrames--);
}
async function handleState(message,current,connection=connectionId){if(connection!==connectionId||message.sequence<=stateSequence)return;const state=await cipher.openState(selfId,connection,message);if(!state||current!==epoch||connection!==connectionId)return;stateSequence=message.sequence;if(!nativeReady){nativeReady=true;for(const id of controlIds)el(id).disabled=false;}
  if(restoreController){restoreController=false;changed();}
  if((state.appliedRevision??0)>=pendingRevision){options={...defaults(),...state.options,memberVoice:state.options.memberVoice||{},memberMusic:state.options.memberMusic||{}};refreshControls();applyGains();}
  el('noiseSuppression').disabled=state.noiseSuppressionAvailable!==true;el('noiseHint').textContent=state.noiseSuppressionAvailable===true?'降噪只处理你的麦克风，音乐保持独立传送。':'当前客户端未提供降噪，请更新到 0.11.0；原有语音与音乐仍可使用。';
  choices('micDevice',state.microphones,options.microphoneId,'系统默认麦克风');choices('outputDevice',state.outputs,options.outputId,'默认听音设备');choices('sourceSelect',state.sources,options.sourceId,'选择一个发声程序');el('micLevel').style.width=Math.min(100,state.microphonePeak*100)+'%';el('overload').hidden=state.speakerPeak<=1&&state.microphonePeak<=1;el('sourceStatus').textContent=state.status;network(state.streams||[]);el('sourceHint').textContent='本机检测到 '+state.sources.length+' 个音源，列表每两秒更新。只共享你选择的程序。';
  screenView.syncSound();
}
let pendingNickname=null;
async function join(){
  if(joined||socket)return;if(!room||!cipher||!/^[a-f0-9]{64}$/.test(token||'')){text('请打开完整的房间邀请');return;}
  const current=++epoch;seenMembers=false;recoveryStarted=0;retryCount=0;memberResume=null;el('join').disabled=true;connectionState('正在连接');text('正在进入音乐房间，入房后自动开启麦克风…');
  try{
    if(!paired){audio=new BrowserAudio(sendAudio,data=>network(data.streams,data.decoded),message=>{text(message);options.microphone=!!audio?.capture[0]&&audio.options.microphone;if(!audio?.capture[1])options.sharing=false;refreshControls();sendFlags();});audio.options={...audio.options,...options};await audio.start();}
    if(current!==epoch)return;openSocket(current);
  }catch(error){if(current===epoch)await leave(error.message||'无法开启房间音频');}
}
function networkLost(ws,code=1006){
  if(socket!==ws)return;ws.cleanup?.();socket=null;wireEpoch++;ws.onclose=null;ws.onmessage=null;try{ws.close(4008,'Reconnecting');}catch{}
  if([1000,4001,4003,4004,4009,4010].includes(code)){void leave(code===4001?'此连接已由新的控制页或连接接管':code===4009?'房间已满，最多 8 人':code===4010?'重连保留时间已结束，请重新加入房间':'房间已关闭或连接权限失效');return;}
  if(!joined||!paired&&!memberResume){void leave('暂时无法连接房间，请检查网络后重试');return;}
  audio?.setNetworkConnected(false);recoveryStarted ||= Date.now();restoreController=paired;
  if(Date.now()-recoveryStarted>=90000){void leave('网络中断超过 90 秒，请重新加入房间');return;}
  connectionState('正在重连',true);text('网络波动，正在自动重连。关麦和音量选择会保留；退出可停止重试。');
  const wait=Math.min(5000,500*2**Math.min(retryCount++,4))*(.8+Math.random()*.2),current=epoch;
  clearTimeout(reconnectTimer);reconnectTimer=setTimeout(()=>{reconnectTimer=null;if(current===epoch&&joined)openSocket(current);},wait);
}
function openSocket(current){
  if(current!==epoch)return;
  const ws=new WebSocket((location.protocol==='https:'?'wss:':'ws:')+'//'+location.host+'/api/rooms/'+room+'/voice');socket=ws;ws.binaryType='arraybuffer';let pending=0,lastSeen=Date.now();
  const timeout=setTimeout(()=>networkLost(ws),15000);
  const ping=setInterval(()=>{if(socket!==ws)return;if(Date.now()-lastSeen>45000){networkLost(ws);return;}if(ws.readyState===WebSocket.OPEN)ws.send(JSON.stringify({type:'ping',at:Date.now()}));},10000);
  heartbeat=ping;ws.cleanup=()=>{clearTimeout(timeout);clearInterval(ping);};
  ws.onopen=()=>{if(current!==epoch||socket!==ws){ws.close();return;}ws.send(JSON.stringify(paired?{type:'join',version:3,role:'controller',memberId:nativeId,controlToken}:{type:'join',version:3,role:'browser',token,name:cleanNickname(el('nickname').value)||'朋友',canResume:true,...(memberResume?{resume:memberResume}:{})}));};
  ws.onmessage=event=>{
    if(current!==epoch||socket!==ws)return;lastSeen=Date.now();
    if(event.data instanceof ArrayBuffer){if(paired||pending>=24)return;pending++;const wire=wireEpoch;receiving=receiving.then(async()=>{const frame=await cipher.openAudio(event.data);if(current!==epoch||wire!==wireEpoch||frame.id===selfId||!roster.some(m=>m.id===frame.id))return;const identity=frame.id+':'+frame.kind;if(frame.sequence<=(lastReceived.get(identity)??-1))return;lastReceived.set(identity,frame.sequence);audio?.receive(frame);}).catch(()=>text('有一路音频未能解密，请确认完整邀请来自同一房间')).finally(()=>pending--);return;}
    let message;try{message=JSON.parse(event.data);}catch{return;}
    if(message.type==='welcome'){
      clearTimeout(timeout);const wasJoined=joined;joined=true;selfId=message.id;
      if(message.resumeToken)memberResume={memberId:selfId,token:message.resumeToken};
      if(paired){connectionId=message.connectionId;stateSequence=-1;controlSequence=0;revision=pendingRevision=0;nativeReady=false;}
      retryCount=0;recoveryStarted=0;enabled(true);audio?.setNetworkConnected(true);refreshControls();
      if(!wasJoined)screenView.join(selfId);else screenView.resume();
      connectionState('已连接');text(paired?'客户端已连接 · 本页控制房间，不重复播放声音':wasJoined?'连接已恢复，保留原有声音与音量设置':'已进入房间 · 正在开启麦克风');
      if(pendingNickname){ws.send(JSON.stringify({type:'rename',name:pendingNickname}));pendingNickname=null;}
      el('nickname').disabled=false;
      if(paired){for(const id of controlIds)el(id).disabled=true;el('microphone').textContent='正在同步麦克风…';channel?.postMessage({type:'nativePlayback'});el('roomHint').textContent='本机专属控制页。昵称会记住，邀请朋友请使用“邀请朋友”。';}
      else{channel?.postMessage({type:'browserJoined'});if(!wasJoined){void setBrowserMicrophone(true);void refreshMicrophones();}else sendFlags();}
    }
    if(message.type==='desktopConnection'){connectionState(message.connected?'已连接':'客户端正在重连',!message.connected);if(!message.connected)text('Windows 客户端正在重连，音量与开关仍可调整；恢复后同步最新选择。');}
    if(typeof message.type==='string'&&message.type.startsWith('screen:'))void screenView.receive(message);
    if(message.type==='members'){if(!paired)for(const cue of membershipCues(roster,message.members,seenMembers))audio?.notify(cue);seenMembers=true;roster=message.members;members();}
    if(message.type==='state'&&paired){const connection=connectionId;receiving=receiving.then(()=>handleState(message,current,connection)).catch(()=>text('本机控制状态解密失败，请从客户端重新打开控制页'));}
  };
  ws.onclose=event=>{ws.cleanup();if(current===epoch)networkLost(ws,event.code);};
  ws.onerror=()=>{if(current===epoch&&socket===ws)text('正在等待网络恢复…');};
}
async function leave(message='已退出房间',explicit=false){
  clearTimeout(reconnectTimer);reconnectTimer=null;clearInterval(heartbeat);recoveryStarted=0;memberResume=null;
  if(explicit&&paired&&joined&&socket?.readyState===WebSocket.OPEN){try{socket.send(JSON.stringify(await cipher.sealControl(selfId,connectionId,controlSequence++,{action:'leave'})));}catch{}}
  screenView.leave();epoch++;wireEpoch++;micRequest++;nativeReady=false;restoreController=false;joined=false;seenMembers=false;lastUnderruns=0;audioPressureUntil=0;clearTimeout(updateTimer);
  const old=socket;socket=null;if(old){old.cleanup?.();old.onclose=null;if(!paired&&old.readyState===WebSocket.OPEN)old.send(JSON.stringify({type:'leave'}));old.close();}
  const oldAudio=audio;audio=null;if(oldAudio)await oldAudio.stop();options={...options,microphone:false,sharing:false};roster=[];members();selfId=null;lastReceived.clear();enabled(false);el('nickname').disabled=false;refreshControls();connectionState('尚未连接');text(message);
}
async function refreshMicrophones(){if(paired||!audio)return;try{const devices=await audio.inputs();choices('micDevice',devices.map(d=>({id:d.deviceId,name:d.label||'麦克风（开启权限后显示名称）'})),options.microphoneId,'系统默认麦克风');}catch{}}
el('join').addEventListener('click',()=>void join());el('leave').addEventListener('click',()=>void leave('已退出房间',true));el('invite').addEventListener('click',async()=>{try{await navigator.clipboard.writeText(simpleInvite());text('入房邀请已复制，不包含你的本机控制权限');}catch{text('请使用 Windows 客户端的“邀请朋友”按钮复制');}});
async function setBrowserMicrophone(enabled){
  if(!joined||!audio)return;const request=++micRequest,current=epoch,engine=audio;options.microphone=enabled;refreshControls();
  try{await engine.microphone(enabled,options.microphoneId);if(request!==micRequest||current!==epoch)return;options.microphone=engine.options.microphone;await refreshMicrophones();changed();text(options.microphone?'已进入房间 · 麦克风已开启':'麦克风已关闭');}
  catch(error){if(request!==micRequest||current!==epoch)return;options.microphone=false;refreshControls();sendFlags();text('已入房，麦克风未开启：'+error.message+'。请允许浏览器使用麦克风后重试。');}
}
el('microphone').addEventListener('click',()=>{if(!joined)return;if(paired){options.microphone=!options.microphone;changed();}else void setBrowserMicrophone(!options.microphone);});
el('saveNickname').addEventListener('click',()=>{const name=cleanNickname(el('nickname').value);if(!name){text('请输入昵称后保存');return;}el('nickname').value=name;const saved=savePreferences({nickname:name});if(joined){if(socket?.readyState===WebSocket.OPEN)socket.send(JSON.stringify({type:'rename',name}));else pendingNickname=name;}el('nickname').blur();text(saved?'昵称已保存，下次自动填写':'昵称已更新；当前浏览器无法保存到本机');});
el('noiseSuppression').addEventListener('click',async()=>{options.noiseSuppression=!options.noiseSuppression;savePreferences({noiseSuppression:options.noiseSuppression});changed();if(!paired&&audio){const result=await audio.noiseSuppression(options.noiseSuppression);el('noiseHint').textContent=result.actual===result.requested?'麦克风降噪已'+(result.actual?'开启':'关闭')+'；音乐不受影响。':result.actual===null?'降噪选择已保存；浏览器未报告实际状态，支持情况取决于设备。':'浏览器未接受全部降噪设置，可使用 Windows 客户端。';}});
el('nickname').addEventListener('keydown',event=>{if(event.key==='Enter'){event.preventDefault();if(joined)el('saveNickname').click();}});
for(const [id,name] of [['micGain','microphoneGain'],['speakerGain','speakerGain'],['musicGain','musicGain']])el(id).addEventListener('input',()=>{options[name]=Number(el(id).value);changed();});
for(const [button,input,delta] of [['micDown','micGain',-10],['micUp','micGain',10],['speakerDown','speakerGain',-10],['speakerUp','speakerGain',10]])el(button).addEventListener('click',()=>{const range=el(input);range.value=Math.min(Number(range.max),Math.max(0,Number(range.value)+delta));range.dispatchEvent(new Event('input'));});
el('talkMode').addEventListener('change',()=>{options.talkMode=el('talkMode').value;el('talkMode').blur();audio?.keyUp();changed();});el('bindKey').addEventListener('click',()=>{bind=true;refreshControls();});
document.addEventListener('keydown',event=>{if(bind){event.preventDefault();const key=event.keyCode;if(key>=8&&key<=254){options.talkKey=key;bind=false;changed();}return;}const editing=event.target.tagName==='TEXTAREA'||event.target.tagName==='SELECT'||event.target.tagName==='INPUT'&&!['range','checkbox','button'].includes(event.target.type);if(!paired&&joined&&!editing){if(options.talkMode==='ptt'&&event.keyCode===options.talkKey)event.preventDefault();audio?.keyDown(event.keyCode);}});document.addEventListener('keyup',event=>{if(event.keyCode===options.talkKey){if(options.talkMode==='ptt')event.preventDefault();audio?.keyUp();}});window.addEventListener('blur',()=>audio?.keyUp());
el('micDevice').addEventListener('change',async()=>{options.microphoneId=el('micDevice').value;if(!paired&&options.microphone){await audio.microphone(false);options.microphone=false;text('已切换输入设备，请主动重新开启麦克风');}changed();});el('outputDevice').addEventListener('change',()=>{options.outputId=el('outputDevice').value;changed();});
el('sourceSelect').addEventListener('change',()=>{options.sourceId=el('sourceSelect').value;changed();});el('share').addEventListener('click',()=>{if(!options.sourceId){text('请先选择一个本机音源');return;}options.sharing=!options.sharing;changed();});el('shareTab').addEventListener('click',async()=>{if(!audio)return;try{if(options.sharing){audio.stopCapture(1);options.sharing=false;}else{await audio.shareTab();options.sharing=!!audio.capture[1];}refreshControls();sendFlags();el('shareTab').textContent=options.sharing?'停止标签页音频共享':'选择浏览器标签页音频';}catch(error){text(error.message);}});
el('autoBuffer').addEventListener('change',()=>{options.autoBuffer=el('autoBuffer').checked;changed();});
if(channel)channel.onmessage=event=>{if(event.data?.type==='browserJoined'&&paired&&joined)channel?.postMessage({type:'nativePlayback'});if(event.data?.type==='nativePlayback'&&!paired&&joined)void leave('Windows 客户端已接管本机房间声音，请使用客户端打开的专属控制页');};
try{navigator.mediaDevices?.setCaptureHandleConfig?.({handle:'voicebridge-room',exposeOrigin:true,permittedOrigins:[location.origin]});}catch{}
window.addEventListener('pagehide',()=>{channel?.close();void leave();});el('nickname').value=preferences.nickname;enabled(false);refreshControls();if(paired){el('nickname').value='本机客户端';el('join').textContent='连接本机房间控制';}
el('invite').disabled=!room||!cipher||!/^[a-f0-9]{64}$/.test(token||'');
if(paired)void join();
