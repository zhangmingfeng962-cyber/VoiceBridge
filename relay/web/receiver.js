"use strict";
(() => {
  const el=id=>document.getElementById(id), room=location.pathname.match(/^\/listen\/([a-f0-9]{32})$/)?.[1];
  const pieces=location.hash.slice(1).split('.'), token=pieces[0], keyHex=pieces[1];
  let context,node,gain,controller,running=false,epoch=0,received=0,after=-1,cryptoKey,lastPacketAt=0;
  const status=(text,live=false)=>{el('status').textContent=text;el('dot').classList.toggle('live',live);};
  const delay=(ms,signal)=>new Promise((resolve,reject)=>{if(signal.aborted){reject(new DOMException('Stopped','AbortError'));return;}const abort=()=>{clearTimeout(timer);reject(new DOMException('Stopped','AbortError'));};const timer=setTimeout(()=>{signal.removeEventListener('abort',abort);resolve();},ms);signal.addEventListener('abort',abort,{once:true});});
  const fromHex=text=>Uint8Array.from(text.match(/../g),s=>parseInt(s,16));
  const names={qqmusic:'QQ音乐',netease:'网易云音乐',kugou:'酷狗音乐',test:'合成验证音 · 未采集真人声音'};
  async function receive(generation,signal) {
    let failures=0;
    while(running&&generation===epoch) {
      try {
        const response=await fetch(`/api/rooms/${room}/next?after=${after}`,{headers:{Authorization:'Bearer '+token},cache:'no-store',signal});
        if([403,404,410].includes(response.status)){await stop();status(response.status===403?'收听密钥无效':'分享已停止或链接已过期');return;}
        if(response.status===204){if(Date.now()-lastPacketAt>8000)status('等待分享者上传音乐…');await delay(150,signal);continue;}
        if(!response.ok)throw new Error('Network');
        const encrypted=await response.arrayBuffer();
        const header=new DataView(encrypted);
        if(encrypted.byteLength!==154276||header.getUint32(0,true)!==0x31454256)throw new Error('Packet');
        const seq=header.getUint32(4,true);
        if(String(seq)!==response.headers.get('X-Sequence')||seq<=after)throw new Error('Sequence');
        const plaintext=await crypto.subtle.decrypt({name:'AES-GCM',iv:new Uint8Array(encrypted,8,12),additionalData:new TextEncoder().encode(room+'|'+seq),tagLength:128},cryptoKey,encrypted.slice(20));
        if(plaintext.byteLength!==154240)throw new Error('Packet');
        if(!running||generation!==epoch)return;
        if(after>=0&&seq!==after+1)node.port.postMessage({type:'reset'});
        after=seq;received+=encrypted.byteLength;lastPacketAt=Date.now();failures=0;
        el('source').textContent=(names[response.headers.get('X-Player')]||'音乐')+' · 48 kHz · 立体声 · 已在本机解密';
        for(let offset=0;offset<plaintext.byteLength;offset+=3856){const data=plaintext.slice(offset,offset+3856);node.port.postMessage({type:'packet',data},[data]);}
      } catch(error) {
        if(signal.aborted||generation!==epoch)return;
        if(error.name==='OperationError'){await stop();status('音乐解密失败，请重新打开分享者提供的完整链接');return;}
        failures++;status(`网络暂时中断，正在重试（${failures}）…`);
        if(failures>=8){await stop();status('网络连接失败，请稍后点击开始收听');return;}
        await delay(Math.min(4000,500*failures),signal).catch(()=>{});
      }
    }
  }
  async function start() {
    if(running){if(context?.state==='suspended')await context.resume();return;}
    if(!room||!/^[a-f0-9]{64}$/.test(token||'')||!/^[a-f0-9]{64}$/.test(keyHex||'')){status('请打开分享者发送的完整收听链接');return;}
    if(!window.isSecureContext||!window.AudioWorkletNode){status('需要新版 Edge / Chrome 和 HTTPS 安全连接');return;}
    running=true;const generation=++epoch;controller=new AbortController();const signal=controller.signal;
    el('start').disabled=true;el('stop').disabled=false;status('正在连接加密音乐…');
    try {
      const audioContext=new AudioContext({sampleRate:48000,latencyHint:'playback'});context=audioContext;
      await audioContext.resume();if(generation!==epoch)return;
      await audioContext.audioWorklet.addModule('/pcm-worklet.js');if(generation!==epoch)return;
      cryptoKey=await crypto.subtle.importKey('raw',fromHex(keyHex),{name:'AES-GCM'},false,['decrypt']);if(generation!==epoch)return;
      node=new AudioWorkletNode(audioContext,'voicebridge-pcm',{numberOfInputs:0,numberOfOutputs:1,outputChannelCount:[2],processorOptions:{bufferMs:Number(el('buffer').value)}});
      gain=audioContext.createGain();gain.gain.value=Number(el('volume').value)/100;node.connect(gain).connect(audioContext.destination);
      node.port.onmessage=event=>{if(generation!==epoch||!running)return;const s=event.data;el('level').style.width=Math.min(100,s.peak*Number(el('volume').value))+'%';el('metrics').textContent=`已解密接收 ${(received/1048576).toFixed(2)} MB · 播放队列 ${Math.round(s.bufferMs)} ms · 缓冲耗尽 ${s.underruns} 次 · 序号中断 ${s.gaps} 次 · 过期队列 ${s.resets} 次。加密不降低音频采样精度。`;if(Date.now()-lastPacketAt<5000)status(audioContext.state!=='running'?'浏览器已暂停，请点击继续收听':s.playing?'正在收听 · 端到端加密':'已连接，正在缓冲…',s.playing&&audioContext.state==='running');};
      audioContext.onstatechange=()=>{if(generation===epoch&&running){el('start').disabled=audioContext.state==='running';el('start').textContent=audioContext.state==='running'?'开始收听':'继续收听';}};
      after=-1;received=0;lastPacketAt=Date.now();void receive(generation,signal);
    }catch{if(generation===epoch){await stop();status('无法开启接收，请确认浏览器允许播放声音后重试');}}
  }
  async function stop() {
    running=false;epoch++;controller?.abort();controller=null;
    node?.disconnect();gain?.disconnect();node=gain=null;const old=context;context=null;
    if(old){old.onstatechange=null;void old.close().catch(()=>{});}
    el('start').disabled=false;el('stop').disabled=true;el('start').textContent='开始收听';el('level').style.width='0';status('已停止收听');
  }
  el('start').addEventListener('click',()=>void start());el('stop').addEventListener('click',()=>void stop());
  el('volume').addEventListener('input',()=>{const value=Number(el('volume').value)/100;el('volumeValue').textContent=Math.round(value*100)+'%';if(gain&&context)gain.gain.setTargetAtTime(value,context.currentTime,.015);});
  el('buffer').addEventListener('change',()=>node?.port.postMessage({type:'configure',bufferMs:Number(el('buffer').value)}));
  window.addEventListener('pagehide',()=>void stop());
})();
