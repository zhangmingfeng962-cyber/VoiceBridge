'use strict';
(() => {
  const el=id=>document.getElementById(id),room=location.pathname.match(/^\/room\/([a-f0-9]{32})$/)?.[1];
  const pieces=location.hash.slice(1).split('.'),token=pieces[0],keyHex=pieces[1],host=pieces[2]==='host';
  let socket,context,processor,voiceGain,stream,source,selfId,generation=0,micGeneration=0,micBusy=false,sequence=0;
  let outgoing=Promise.resolve(),pendingFrames=0,incoming=Promise.resolve(),pendingIncoming=0,members=[],keys=new Map(),speaking=new Map(),lastSequences=new Map();
  const text=message=>{el('voiceStatus').textContent=message;};
  const fromHex=value=>Uint8Array.from(value.match(/../g),s=>parseInt(s,16));
  const toHex=value=>[...value].map(v=>v.toString(16).padStart(2,'0')).join('');
  const aad=(id,seq)=>new TextEncoder().encode(room+'|voice|'+id+'|'+seq);
  const virtualInput=label=>/cable\s*output|voicemeeter|stereo\s*mix|立体声混音/i.test(label||'');
  async function refreshInputs(){
    try{
      const selected=el('voiceInput').value,devices=await navigator.mediaDevices.enumerateDevices?.()||[];
      el('voiceInput').replaceChildren();const option=document.createElement('option');option.value='';option.textContent='系统默认麦克风';el('voiceInput').append(option);
      for(const device of devices.filter(d=>d.kind==='audioinput'&&d.deviceId!=='default'&&d.deviceId!=='communications')){
        const item=document.createElement('option');item.value=device.deviceId;item.textContent=(device.label||'麦克风（允许权限后显示名称）')+(virtualInput(device.label)?' · 虚拟输入':'');el('voiceInput').append(item);
      }
      el('voiceInput').value=selected||'';
    }catch{text('无法读取设备列表，可使用浏览器的麦克风设置选择输入');}
  }
  function keyFor(id){
    if(!keys.has(id))keys.set(id,(async()=>{
      const base=await crypto.subtle.importKey('raw',fromHex(keyHex),'HKDF',false,['deriveKey']);
      return crypto.subtle.deriveKey({name:'HKDF',hash:'SHA-256',salt:new TextEncoder().encode(room),info:new TextEncoder().encode('VoiceBridge voice v1|'+id)},base,{name:'AES-GCM',length:256},false,['encrypt','decrypt']);
    })());return keys.get(id);
  }
  function render(){
    el('members').replaceChildren();
    for(const member of members){const row=document.createElement('li');const active=Date.now()-(speaking.get(member.id)||0)<600;row.textContent=(active?'● ':'○ ')+member.name+(member.id===selfId?'（你）':'')+' · '+(member.microphone?'麦克风开启':'麦克风关闭');row.className=active?'talking':'';el('members').append(row);}
    el('memberCount').textContent=members.length+'/8';
  }
  async function closeMicrophone(){
    micGeneration++;processor?.port.postMessage({type:'capture',enabled:false});source?.disconnect();source=null;
    for(const track of stream?.getTracks()||[])track.stop();stream=null;
    if(socket?.readyState===WebSocket.OPEN&&selfId)socket.send(JSON.stringify({type:'microphone',enabled:false}));
    el('microphone').textContent='开启麦克风';el('microphone').setAttribute('aria-pressed','false');
  }
  async function toggleMicrophone(){
    if(stream){await closeMicrophone();text('麦克风已关闭，可以继续听朋友和音乐');return;}
    if(micBusy||!selfId||socket?.readyState!==WebSocket.OPEN)return;
    micBusy=true;const current=++micGeneration;el('microphone').disabled=true;text('等待你允许麦克风…');
    try{
      const selected=el('voiceInput').value;
      const candidate=await navigator.mediaDevices.getUserMedia({audio:{...(selected?{deviceId:{exact:selected}}:{}),channelCount:1,echoCancellation:true,noiseSuppression:true,autoGainControl:false},video:false});
      if(current!==micGeneration||!context||socket?.readyState!==WebSocket.OPEN){candidate.getTracks().forEach(t=>t.stop());return;}
      await refreshInputs();
      if(current!==micGeneration||!context||socket?.readyState!==WebSocket.OPEN){candidate.getTracks().forEach(t=>t.stop());return;}
      if(candidate.getAudioTracks().some(track=>virtualInput(track.label))){candidate.getTracks().forEach(t=>t.stop());text('当前选到了虚拟输入，已停止采集。请在麦克风列表选真实麦克风后重新开启。');return;}
      stream=candidate;await context.resume();source=context.createMediaStreamSource(stream);source.connect(processor);
      for(const track of stream.getAudioTracks())track.onended=()=>void closeMicrophone();
      socket.send(JSON.stringify({type:'microphone',enabled:true}));processor.port.postMessage({type:'capture',enabled:true,id:current});
      el('microphone').textContent='关闭麦克风';el('microphone').setAttribute('aria-pressed','true');text('麦克风已开启 · 仅传送人声，音乐走独立通道');
    }catch(error){await closeMicrophone();text(error.name==='NotAllowedError'?'未获得麦克风权限，仍可收听房间':'麦克风无法开启，请检查浏览器和系统输入设备');}
    finally{micBusy=false;el('microphone').disabled=!selfId;}
  }
  function upload(pcm,current,captureId){
    if(captureId!==micGeneration||!stream||socket?.readyState!==WebSocket.OPEN||pendingFrames>=4||socket.bufferedAmount>32768)return;
    pendingFrames++;outgoing=outgoing.then(async()=>{
      if(current!==generation||captureId!==micGeneration||!stream||socket?.readyState!==WebSocket.OPEN)return;
      const id=selfId,seq=sequence++,iv=crypto.getRandomValues(new Uint8Array(12));
      const encrypted=await crypto.subtle.encrypt({name:'AES-GCM',iv,additionalData:aad(id,seq)},await keyFor(id),pcm);
      if(current!==generation||captureId!==micGeneration||!stream||socket?.readyState!==WebSocket.OPEN)return;
      const packet=new Uint8Array(20+encrypted.byteLength),view=new DataView(packet.buffer);view.setUint32(0,0x31564256,true);view.setUint32(4,seq,true);packet.set(iv,8);packet.set(new Uint8Array(encrypted),20);socket.send(packet);
    }).catch(()=>text('语音发送失败，请离开后重新进入房间')).finally(()=>pendingFrames--);
  }
  async function receiveVoice(data,current){
    if(data.byteLength!==692)return;const id=toHex(new Uint8Array(data,0,16)),view=new DataView(data,16),seq=view.getUint32(4,true);
    if(view.getUint32(0,true)!==0x31564256||id===selfId||seq<=(lastSequences.get(id)??-1))return;
    try{
      const pcm=await crypto.subtle.decrypt({name:'AES-GCM',iv:new Uint8Array(data,24,12),additionalData:aad(id,seq)},await keyFor(id),data.slice(36));
      if(current!==generation||!processor||pcm.byteLength!==640||!members.some(member=>member.id===id))return;
      lastSequences.set(id,seq);
      const values=new Float32Array(320),samples=new DataView(pcm);let peak=0;
      for(let i=0;i<320;i++){values[i]=samples.getInt16(i*2,true)/32768;peak=Math.max(peak,Math.abs(values[i]));}
      if(peak>.015)speaking.set(id,Date.now());processor.port.postMessage({type:'voice',id,sequence:seq,samples:values.buffer},[values.buffer]);
    }catch{text('无法解密房间语音，请确认使用同一个完整邀请链接');}
  }
  async function leave(message='已离开房间'){
    generation++;selfId=null;await closeMicrophone();const oldSocket=socket;socket=null;if(oldSocket){oldSocket.onclose=null;oldSocket.close();}
    processor?.disconnect();voiceGain?.disconnect();processor=voiceGain=null;const old=context;context=null;if(old)void old.close().catch(()=>{});
    members=[];speaking.clear();keys.clear();lastSequences.clear();render();el('join').disabled=false;el('leave').disabled=true;el('microphone').disabled=true;
    el('stop').click();text(message);
  }
  async function join(){
    if(socket?.readyState===WebSocket.OPEN){await context?.resume();return;}
    if(!room||!/^[a-f0-9]{64}$/.test(token||'')||!/^[a-f0-9]{64}$/.test(keyHex||'')){text('请使用主持人提供的完整房间链接');return;}
    if(!window.isSecureContext||!window.AudioWorkletNode){text('需要新版桌面 Edge / Chrome 和 HTTPS');return;}
    const current=++generation;el('join').disabled=true;el('leave').disabled=false;text('正在进入房间，麦克风保持关闭…');
    try{
      context=new AudioContext({sampleRate:48000,latencyHint:'interactive'});await context.resume();await context.audioWorklet.addModule('/voice-worklet.js');
      if(current!==generation)return;
      processor=new AudioWorkletNode(context,'voicebridge-voice',{numberOfInputs:1,numberOfOutputs:1,outputChannelCount:[1],channelCount:1,channelCountMode:'explicit'});
      voiceGain=context.createGain();voiceGain.gain.value=Number(el('voiceVolume').value)/100;processor.connect(voiceGain).connect(context.destination);
      processor.port.onmessage=({data:m})=>{if(m.type==='microphone')upload(m.pcm,current,m.id);};
      const ws=new WebSocket(`${location.protocol==='https:'?'wss:':'ws:'}//${location.host}/api/rooms/${room}/voice`);socket=ws;ws.binaryType='arraybuffer';
      const deadline=setTimeout(()=>{if(current===generation&&!selfId)void leave('房间连接超时，请重新进入');},12000);
      ws.onopen=()=>{if(current!==generation){ws.close();return;}ws.send(JSON.stringify({type:'join',token,name:el('nickname').value.trim()||'朋友'}));};
      ws.onmessage=event=>{
        if(current!==generation)return;
        if(event.data instanceof ArrayBuffer){if(pendingIncoming>=64)return;pendingIncoming++;incoming=incoming.then(()=>receiveVoice(event.data,current)).catch(()=>{}).finally(()=>pendingIncoming--);return;}
        let message;try{message=JSON.parse(event.data);}catch{return;}
        if(message.type==='welcome'){clearTimeout(deadline);selfId=message.id;sequence=0;el('microphone').disabled=false;text('已进入房间 · 麦克风关闭');el('start').click();}
        if(message.type==='members'){const ids=new Set(message.members.map(p=>p.id));for(const member of members)if(!ids.has(member.id)){processor?.port.postMessage({type:'remove',id:member.id});keys.delete(member.id);}members=message.members;render();}
      };
      ws.onclose=event=>{clearTimeout(deadline);if(current===generation)void leave(event.code===4004?'主持人已关闭房间':event.code===4009?'房间已满，最多 8 人':'房间连接已断开，麦克风已关闭，请重新进入');};
      ws.onerror=()=>text('房间连接失败，请检查网络');
    }catch{if(current===generation)await leave('无法打开房间音频，请检查浏览器播放权限');}
  }
  el('join').addEventListener('click',()=>void join());el('leave').addEventListener('click',()=>void leave());el('microphone').addEventListener('click',()=>void toggleMicrophone());
  el('refreshInputs').addEventListener('click',()=>void refreshInputs());
  el('voiceInput').addEventListener('change',()=>{if(stream){void closeMicrophone();text('麦克风设备已更换，请重新点击开启');}});
  void refreshInputs();
  el('voiceVolume').addEventListener('input',()=>{const value=Number(el('voiceVolume').value);el('voiceVolumeValue').textContent=value+'%';if(voiceGain&&context)voiceGain.gain.setTargetAtTime(value/100,context.currentTime,.015);});
  if(host){el('volume').value='0';el('volume').dispatchEvent(new Event('input'));el('hostNotice').hidden=false;el('nickname').value='主持人';}
  const refresh=setInterval(render,300);window.addEventListener('pagehide',()=>{clearInterval(refresh);void leave();});
})();
