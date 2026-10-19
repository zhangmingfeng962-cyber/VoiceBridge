import {RoomCrypto} from './crypto.js';
import {BrowserAudio} from './audio.js';
import {membershipCues} from './room-events.js';
const el=id=>document.getElementById(id),room=location.pathname.match(/^\/room\/([a-f0-9]{32})$/)?.[1],parts=location.hash.slice(1).split('.');
const token=parts[0],key=parts[1],paired=parts[2]==='desktop',nativeId=parts[3],controlToken=parts[4];
const cipher=room&&/^[a-f0-9]{64}$/.test(key||'')?new RoomCrypto(room,key):null;
const defaults=()=>({microphone:false,microphoneId:'',outputId:'',microphoneGain:100,speakerGain:parts.includes('silent')?0:100,talkMode:'free',talkKey:86,sharing:false,sourceId:'',musicGain:100,autoBuffer:true,memberVoice:{},memberMusic:{}});
let options=defaults(),socket,audio,selfId,connectionId,joined=false,epoch=0,revision=0,pendingRevision=0,controlSequence=0,stateSequence=-1,sendSequence=[0,0],sending=Promise.resolve(),receiving=Promise.resolve(),controlSending=Promise.resolve(),pendingFrames=0,bind=false,updateTimer,roster=[],seenMembers=false,micRequest=0,nativeReady=false;
const cards=new Map(),lastReceived=new Map();
const channel=window.BroadcastChannel&&room?new BroadcastChannel('voicebridge-room:'+room):null;
const text=message=>{el('status').textContent=message;};
const keyName=code=>code>=65&&code<=90?String.fromCharCode(code):code>=48&&code<=57?String.fromCharCode(code):code>=112&&code<=135?'F'+(code-111):({32:'空格',16:'Shift',17:'Ctrl',18:'Alt',9:'Tab'})[code]||'键 '+code;
const simpleInvite=()=>location.origin+'/room/'+room+'#'+token+'.'+key;
const controlIds=['microphone','talkMode','bindKey','micDevice','outputDevice','micGain','micDown','micUp','speakerGain','speakerDown','speakerUp','sourceSelect','share','shareTab','musicGain','autoBuffer'];
function enabled(value){for(const id of controlIds)el(id).disabled=!value;el('saveNickname').disabled=!value;el('join').disabled=value;el('leave').disabled=!value;}
function refreshControls(){
  el('microphone').textContent=options.microphone?'关闭麦克风':'开启麦克风';el('microphone').setAttribute('aria-pressed',String(options.microphone));
  el('share').textContent=options.sharing?'停止共享音乐':'开始共享音乐';el('share').setAttribute('aria-pressed',String(options.sharing));
  for(const [id,name] of [['micGain','microphoneGain'],['speakerGain','speakerGain'],['musicGain','musicGain']]){if(document.activeElement!==el(id))el(id).value=options[name];el(id+'Value').textContent=Math.round(options[name])+'%';}
  el('talkMode').value=options.talkMode;el('bindKey').textContent=bind?'按下要设置的按键…':'说话键：'+keyName(options.talkKey);el('autoBuffer').checked=options.autoBuffer;
  el('pttHint').textContent=options.talkMode==='ptt'?(paired?'客户端运行时，游戏后台也可按住 '+keyName(options.talkKey)+' 说话。':'网页模式需保持页面焦点，按住 '+keyName(options.talkKey)+' 说话；切走页面会停止发送。'):'自由发言：麦克风开启后持续传送人声，讲话不会压低音乐。';
  el('share').hidden=!paired;el('shareTab').hidden=paired;el('outputWrap').hidden=!paired;el('clientHelp').hidden=paired;
  if(!paired){el('sourceSelect').disabled=true;el('sourceHint').textContent='网页可选择其他浏览器标签页的音频；需要自动识别微信等桌面程序时，请使用 Windows 客户端加入本房间。';}
}
function choices(id,items,selected,empty){const select=el(id),signature=JSON.stringify(items);if(select.dataset.signature!==signature){select.replaceChildren();if(empty){const option=document.createElement('option');option.value='';option.textContent=empty;select.append(option);}for(const item of items){const option=document.createElement('option');option.value=item.id;option.textContent=item.name+(item.active?' · 正在发声':'');select.append(option);}select.dataset.signature=signature;}select.value=selected;if(select.selectedIndex<0)select.selectedIndex=0;}
function applyGains(){audio?.configure(options);for(const member of roster){if(member.id===selfId)continue;const card=cards.get(member.id);if(!card)continue;for(const [kind,field] of [[0,'memberVoice'],[1,'memberMusic']]){const value=options[field]?.[member.id]??100,input=card.querySelector(`[data-kind="${kind}"]`),out=card.querySelector(`[data-value="${kind}"]`);if(document.activeElement!==input)input.value=value;out.textContent=Math.round(value)+'%';}}}
function members(){
  const mine=roster.find(m=>m.id===selfId);if(mine&&document.activeElement!==el('nickname'))el('nickname').value=mine.name;
  const present=new Set(roster.map(m=>m.id));for(const [id,card] of cards)if(!present.has(id)){card.remove();cards.delete(id);audio?.remove(id);}
  for(const member of roster){
    let card=cards.get(member.id);if(!card){card=document.createElement('section');card.className='member';const header=document.createElement('div');header.className='memberHead';const name=document.createElement('span');name.className='memberName';const mute=document.createElement('button');mute.className='mini';mute.textContent='静音此人';mute.addEventListener('click',()=>{options.memberVoice[member.id]=(options.memberVoice[member.id]??100)>0?0:100;applyGains();changed();});header.append(name,mute);const detail=document.createElement('p');detail.className='memberStatus';card.append(header,detail);
      for(const [kind,title] of [[0,'此人人声'],[1,'此人音乐']]){const label=document.createElement('label');label.textContent=title;const output=document.createElement('output');output.dataset.value=kind;label.append(output);const input=document.createElement('input');input.type='range';input.min=0;input.max=kind===0?200:100;input.value=100;input.dataset.kind=kind;input.setAttribute('aria-label',title+'音量');input.addEventListener('input',()=>{options[kind===0?'memberVoice':'memberMusic'][member.id]=Number(input.value);output.textContent=input.value+'%';audio?.setGain(member.id,kind,Number(input.value));changed();});card.append(label,input);}el('members').append(card);cards.set(member.id,card);
    }
    card.querySelector('.memberName').textContent=member.name+(member.id===selfId?'（你）':'');card.querySelector('.memberStatus').textContent=(member.microphone?'麦克风开启':'麦克风关闭')+(member.music?' · 正在共享 '+(member.source||'音乐'):'');
    const self=member.id===selfId;for(const child of card.querySelectorAll('label,input,button'))child.hidden=self;
    card.classList.toggle('off',!member.microphone&&!member.music);
  }
  el('onlineCount').textContent=roster.length+' / 8 人';applyGains();
}
function network(streams,decoded){const music=streams.filter(s=>s.id.endsWith(':1')),target=Math.max(0,...(music.length?music:streams).map(s=>s.targetMs));const under=streams.reduce((n,s)=>n+s.underruns,0);el('network').textContent=streams.length?(options.autoBuffer?'自动缓冲':'当前缓冲')+' · '+Math.round(target)+' ms'+(under?' · 曾等待补充缓冲 '+under+' 次':' · 连续接收中'):'等待其他成员发声或共享音乐';el('metrics').textContent='48 kHz · 立体声音乐 · Opus 20 ms 小帧'+(decoded!==undefined?' · 已解码 '+decoded+' 帧':' · 客户端独立播放');}
function changed(){refreshControls();applyGains();if(!joined)return;if(paired){pendingRevision=++revision;clearTimeout(updateTimer);updateTimer=setTimeout(()=>{const message={action:'configure',revision:pendingRevision,options:structuredClone(options)},current=epoch;controlSending=controlSending.then(async()=>{const sealed=await cipher.sealControl(selfId,connectionId,controlSequence++,message);if(current===epoch&&socket?.readyState===WebSocket.OPEN)socket.send(JSON.stringify(sealed));}).catch(()=>text('控制发送失败，请重新打开客户端的控制页'));},70);}else{audio?.configure(options);sendFlags();}}
function sendFlags(){if(socket?.readyState===WebSocket.OPEN&&!paired)socket.send(JSON.stringify({type:'flags',microphone:options.microphone,music:options.sharing,source:options.sharing?'浏览器标签页':''}));}
function sendAudio(kind,opus,captureEpoch){
  if(!joined||paired||pendingFrames>=8||socket?.bufferedAmount>32768)return;const current=epoch;pendingFrames++;
  sending=sending.then(async()=>{if(current!==epoch||!audio)return;if(kind===0&&(!options.microphone||captureEpoch!==audio.micEpoch)||kind===1&&(!options.sharing||captureEpoch!==audio.shareEpoch))return;const packet=await cipher.sealAudio(kind,selfId,sendSequence[kind]++,opus);if(current===epoch&&socket?.readyState===WebSocket.OPEN&&(kind===1?captureEpoch===audio?.shareEpoch:captureEpoch===audio?.micEpoch))socket.send(packet);}).catch(()=>text('音频发送失败，请重新进入房间')).finally(()=>pendingFrames--);
}
async function handleState(message,current){if(message.sequence<=stateSequence)return;const state=await cipher.openState(selfId,connectionId,message);if(!state||current!==epoch)return;stateSequence=message.sequence;if(!nativeReady){nativeReady=true;for(const id of controlIds)el(id).disabled=false;}
  if((state.appliedRevision??0)>=pendingRevision){options={...defaults(),...state.options,memberVoice:state.options.memberVoice||{},memberMusic:state.options.memberMusic||{}};refreshControls();applyGains();}
  choices('micDevice',state.microphones,options.microphoneId,'系统默认麦克风');choices('outputDevice',state.outputs,options.outputId,'默认听音设备');choices('sourceSelect',state.sources,options.sourceId,'选择一个发声程序');el('micLevel').style.width=Math.min(100,state.microphonePeak*100)+'%';el('overload').hidden=state.speakerPeak<=1&&state.microphonePeak<=1;el('sourceStatus').textContent=state.status;network(state.streams||[]);el('sourceHint').textContent='本机检测到 '+state.sources.length+' 个音源，列表每两秒更新。只共享你选择的程序。';
}
async function join(){
  if(joined)return;if(!room||!cipher||!/^[a-f0-9]{64}$/.test(token||'')){text('请打开完整的房间邀请');return;}
  const current=++epoch;seenMembers=false;el('join').disabled=true;text('正在进入音乐房间，入房后自动开启麦克风…');
  try{
    if(!paired){audio=new BrowserAudio(sendAudio,data=>network(data.streams,data.decoded),message=>{text(message);options.microphone=!!audio?.capture[0]&&audio.options.microphone;if(!audio?.capture[1])options.sharing=false;refreshControls();sendFlags();});audio.options={...audio.options,...options};await audio.start();}
    if(current!==epoch)return;const ws=new WebSocket(`${location.protocol==='https:'?'wss:':'ws:'}//${location.host}/api/rooms/${room}/voice`);socket=ws;ws.binaryType='arraybuffer';let pending=0;
    const timeout=setTimeout(()=>{if(current===epoch&&!joined)void leave('连接超时，请确认主持人客户端仍在运行');},15000);
    ws.onopen=()=>{if(current!==epoch){ws.close();return;}ws.send(JSON.stringify(paired?{type:'join',version:3,role:'controller',memberId:nativeId,controlToken}:{type:'join',version:3,role:'browser',token,name:el('nickname').value.trim()||'朋友'}));};
    ws.onmessage=event=>{
      if(current!==epoch)return;
      if(event.data instanceof ArrayBuffer){if(paired||pending>=24)return;pending++;receiving=receiving.then(async()=>{const frame=await cipher.openAudio(event.data);if(current!==epoch||frame.id===selfId||!roster.some(m=>m.id===frame.id))return;const identity=frame.id+':'+frame.kind;if(frame.sequence<=(lastReceived.get(identity)??-1))return;lastReceived.set(identity,frame.sequence);audio?.receive(frame);}).catch(()=>text('有一路音频未能解密，请确认完整邀请来自同一房间')).finally(()=>pending--);return;}
      let message;try{message=JSON.parse(event.data);}catch{return;}
      if(message.type==='welcome'){clearTimeout(timeout);joined=true;selfId=message.id;connectionId=message.connectionId;stateSequence=-1;controlSequence=0;revision=pendingRevision=0;enabled(true);refreshControls();text(paired?'客户端已连接 · 本页只控制，不重复播放房间声音':'已进入房间 · 正在开启麦克风');el('nickname').disabled=false;if(paired){nativeReady=false;for(const id of controlIds)el(id).disabled=true;el('microphone').textContent='正在读取麦克风…';channel?.postMessage({type:'nativePlayback'});el('roomHint').textContent='本机专属控制页。邀请朋友请使用下方邀请按钮。';}else {channel?.postMessage({type:'browserJoined'});void setBrowserMicrophone(true);void refreshMicrophones();}}
      if(message.type==='members'){if(!paired)for(const cue of membershipCues(roster,message.members,seenMembers))audio?.notify(cue);seenMembers=true;roster=message.members;members();}
      if(message.type==='state'&&paired)receiving=receiving.then(()=>handleState(message,current)).catch(()=>text('本机控制状态解密失败，请从客户端重新打开控制页'));
    };
    ws.onclose=event=>{clearTimeout(timeout);if(current===epoch)void leave(event.code===4001?'此控制页已由新的控制页接管':event.code===4009?'房间已满，最多 8 人':'房间已断开，麦克风和共享已关闭');};ws.onerror=()=>text('连接失败，请检查网络或主持人的客户端');
  }catch(error){if(current===epoch)await leave(error.message||'无法开启房间音频');}
}
async function leave(message='已退出房间',explicit=false){
  if(explicit&&paired&&joined&&socket?.readyState===WebSocket.OPEN){try{socket.send(JSON.stringify(await cipher.sealControl(selfId,connectionId,controlSequence++,{action:'leave'})));}catch{}}
  epoch++;micRequest++;nativeReady=false;joined=false;seenMembers=false;clearTimeout(updateTimer);const old=socket;socket=null;if(old){old.onclose=null;old.close();}const oldAudio=audio;audio=null;if(oldAudio)await oldAudio.stop();options={...options,microphone:false,sharing:false};roster=[];members();selfId=null;lastReceived.clear();enabled(false);el('nickname').disabled=false;refreshControls();text(message);
}
async function refreshMicrophones(){if(paired||!audio)return;try{const devices=await audio.inputs();choices('micDevice',devices.map(d=>({id:d.deviceId,name:d.label||'麦克风（开启权限后显示名称）'})),options.microphoneId,'系统默认麦克风');}catch{}}
el('join').addEventListener('click',()=>void join());el('leave').addEventListener('click',()=>void leave('已退出房间',true));el('invite').addEventListener('click',async()=>{try{await navigator.clipboard.writeText(simpleInvite());text('入房邀请已复制，不包含你的本机控制权限');}catch{text('请使用 Windows 客户端的“邀请朋友”按钮复制');}});
async function setBrowserMicrophone(enabled){
  if(!joined||!audio)return;const request=++micRequest,current=epoch,engine=audio;options.microphone=enabled;refreshControls();
  try{await engine.microphone(enabled,options.microphoneId);if(request!==micRequest||current!==epoch)return;options.microphone=engine.options.microphone;await refreshMicrophones();changed();text(options.microphone?'已进入房间 · 麦克风已开启':'麦克风已关闭');}
  catch(error){if(request!==micRequest||current!==epoch)return;options.microphone=false;refreshControls();sendFlags();text('已入房，麦克风未开启：'+error.message+'。请允许浏览器使用麦克风后重试。');}
}
el('microphone').addEventListener('click',()=>{if(!joined)return;if(paired){options.microphone=!options.microphone;changed();}else void setBrowserMicrophone(!options.microphone);});
el('saveNickname').addEventListener('click',()=>{const name=el('nickname').value.trim();if(!joined||!name){text('请输入昵称后保存');return;}socket.send(JSON.stringify({type:'rename',name}));el('nickname').blur();text('昵称已更新');});
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
window.addEventListener('pagehide',()=>{channel?.close();void leave();});enabled(false);refreshControls();if(paired){el('nickname').value='本机客户端';el('join').textContent='连接本机房间控制';}
el('invite').disabled=!room||!cipher||!/^[a-f0-9]{64}$/.test(token||'');
if(paired)void join();
