import {ScreenShare} from './screen.js';
const byId=id=>document.getElementById(id);
const names={window:'软件窗口',monitor:'桌面／显示器',browser:'浏览器标签页'};

export class ScreenView{
  constructor({send,sound,setSound,setVolume,setSource,setListening,audioPressure}){
    Object.assign(this,{send,sound,setSound,setVolume,setSource,setListening,audioPressure});this.engine=null;this.joined=false;this.state={owner:null};
    byId('screenStart').onclick=()=>void this.engine?.select();
    byId('screenChange').onclick=()=>void this.engine?.select(true);
    byId('screenPause').onclick=()=>void this.engine?.pause();
    byId('screenStop').onclick=()=>this.engine?.stop();
    byId('screenWatch').onclick=()=>this.engine?.setWatching(!this.engine.watching);
    byId('screenRetry').onclick=()=>this.engine?.setWatching(true);
    byId('screenQuality').onchange=()=>void this.engine?.quality(byId('screenQuality').value,byId('screenAuto').checked);
    byId('screenAuto').onchange=()=>void this.engine?.quality(byId('screenQuality').value,byId('screenAuto').checked);
    byId('screenSound').onclick=async()=>{try{await this.setSound(!this.sound().sharing);}catch(error){this.status(error.message);}this.syncSound();};
    byId('screenSoundGain').oninput=()=>{this.setVolume(Number(byId('screenSoundGain').value));this.syncSound();};
    byId('screenSource').onchange=()=>this.setSource(byId('screenSource').value);
    byId('screenListenGain').oninput=()=>{if(this.state.owner)this.setListening(this.state.owner,Number(byId('screenListenGain').value));this.syncSound();};
    byId('screenExpand').onclick=()=>{const panel=byId('screenPanel');panel.classList.toggle('screenExpanded');byId('screenExpand').textContent=panel.classList.contains('screenExpanded')?'还原布局':'放大画面';};
    byId('screenFullscreen').onclick=async()=>{try{await byId('screenStage').requestFullscreen();}catch{this.status('浏览器未允许全屏，可以使用“放大画面”');}};
    byId('screenExitFullscreen').onclick=()=>void document.exitFullscreen();
    document.addEventListener('fullscreenchange',()=>{byId('screenExitFullscreen').hidden=document.fullscreenElement!==byId('screenStage');});
    byId('screenFit').onclick=()=>{const cover=byId('screenVideo').style.objectFit==='cover';byId('screenVideo').style.objectFit=cover?'contain':'cover';byId('screenFit').textContent=cover?'填满画面':'适应窗口';};
    this.render();
  }
  join(selfId){
    this.leave();this.joined=true;
    this.engine=new ScreenShare({selfId,send:this.send,onState:state=>{this.state=state;this.render();},onVideo:(stream,local)=>{
      const video=byId('screenVideo');video.srcObject=stream;video.muted=true;this.hasVideo=!!stream;this.localPreview=local;
      if(stream)void video.play().catch(()=>this.status('画面已收到，可点击“重连画面”重新播放'));this.render();
    },onStatus:message=>this.status(message),onMetrics:metric=>this.metrics(metric)});
    this.engine.audioPressure=this.audioPressure;this.render();
  }
  receive(message){return this.engine?.receive(message);}
  status(message){this.message=message;byId('screenStatus').textContent=message;}
  metrics(metric){
    if(!this.state.owner)return;
    const resolution=metric.width?` · ${metric.width} × ${metric.height}${metric.fps?' / '+metric.fps+' 帧':''}`:'';
    byId('screenNetwork').textContent=(metric.relay?'服务器中继':'直连')+(metric.rttMs?' · 网络往返 '+metric.rttMs+' ms':'')+resolution+(metric.owner&&metric.bitrate?' · 此路码率 '+Math.round(metric.bitrate/1000)+' kbps':'');
  }
  syncSound(){
    const value=this.sound(),own=this.state.owner&&this.state.self;
    byId('screenOwnSound').hidden=!!this.state.owner&&!own;
    byId('screenViewerSound').hidden=!this.state.owner||own;
    byId('screenSound').textContent=value.sharing?'共享声音：开':'共享声音：关';byId('screenSound').setAttribute('aria-pressed',String(!!value.sharing));
    byId('screenSound').disabled=!this.joined||!value.ready;
    if(document.activeElement!==byId('screenSoundGain'))byId('screenSoundGain').value=value.volume;
    byId('screenSoundGainValue').textContent=value.volume+'%';byId('screenSoundGain').disabled=!this.joined||!value.ready;
    const source=byId('sourceSelect'),copy=byId('screenSource'),signature=[...source.options].map(o=>[o.value,o.textContent]);
    if(copy.dataset.items!==JSON.stringify(signature)){copy.replaceChildren(...[...source.options].map(o=>o.cloneNode(true)));copy.dataset.items=JSON.stringify(signature);}
    copy.value=value.sourceId;copy.disabled=!this.joined||!value.program||!value.ready;
    byId('screenSoundHint').textContent=value.program?'选择一个程序作为共享声音。它与下方音乐共享使用同一通道，只发送一次；不会捕获整个系统声音。':'网页共享声音需另外选择其他浏览器标签页。共享桌面程序声音时请使用 Windows 客户端。';
    const listen=value.listening?.[this.state.owner]??100;
    if(document.activeElement!==byId('screenListenGain'))byId('screenListenGain').value=listen;
    byId('screenListenGainValue').textContent=listen+'%';
  }
  render(){
    const s=this.state,own=!!s.owner&&s.self,other=!!s.owner&&!own,ready=this.joined&&s.supported;
    const statusKey=!this.joined?'offline':!s.supported?'unsupported':s.session||'idle';if(this.statusKey!==statusKey){this.statusKey=statusKey;this.message='';}
    const compatible=typeof globalThis.RTCPeerConnection==='function',capture=!!navigator.mediaDevices?.getDisplayMedia;
    byId('screenStart').disabled=!ready||!!s.owner||s.selecting||!compatible||!capture;byId('screenStart').textContent=s.selecting?'正在选择画面…':'共享软件窗口／桌面';
    for(const id of ['screenChange','screenPause','screenStop'])byId(id).hidden=!own&&!s.selecting;
    byId('screenChange').disabled=!own||s.selecting;byId('screenPause').disabled=!own||s.selecting;byId('screenStop').disabled=!own&&!s.selecting;
    byId('screenPause').textContent=s.paused?'恢复画面':'暂停画面';
    byId('screenWatch').hidden=!other;byId('screenWatch').textContent=s.watching?'停止观看':'观看画面';
    byId('screenRetry').hidden=!other||s.paused;
    byId('screenQuality').disabled=!ready||other;byId('screenAuto').disabled=!ready||other;
    byId('screenQuality').value=s.mode||'balanced';byId('screenAuto').checked=s.auto!==false;
    byId('screenOwner').textContent=s.owner?(own?'你正在共享':s.name+' 正在共享')+(s.surface?' · '+(names[s.surface]||'画面'):''):'房间内任何成员都可以共享';
    byId('screenLive').textContent=s.owner?(s.paused?'已暂停':'共享中'):'未共享';byId('screenLive').classList.toggle('paused',!!s.paused);
    byId('screenViewers').textContent=s.owner?(s.viewers?.length||0)+' 人观看':'';
    const placeholder=!s.owner||s.paused||!this.hasVideo||other&&!s.watching;
    byId('screenOverlay').hidden=!placeholder;
    byId('screenOverlayTitle').textContent=!s.owner?'把画面分享给房间里的朋友':s.paused?'共享已暂停':other&&!s.watching?'你已停止观看':'正在连接画面…';
    byId('screenOverlayDetail').textContent=!s.owner?'选择软件窗口或显示器，朋友无需重新加入房间。':s.paused?'恢复共享后继续观看，声音由独立开关控制。':other&&!s.watching?'语音和音乐仍可继续，随时点击“观看画面”。':'网络连通后自动显示；你可以继续讲话和听音乐。';
    for(const id of ['screenExpand','screenFullscreen','screenFit'])byId(id).disabled=!s.owner;
    const baseline=!this.joined?'进入房间后可以共享或观看画面。':!s.supported?'正在读取屏幕共享能力；旧版房间服务需要升级后才可共享。':!compatible?'此浏览器不支持实时视频，请使用新版 Edge 或 Chrome。':!s.owner?'尚无人共享。每次开始或更换来源，都由你在系统选择器中确认。':'正在连接画面…';
    byId('screenStatus').textContent=this.message||baseline;
    byId('screenRelay').textContent=ready?(s.relayAvailable?'支持跨网直连和服务器中继':'当前仅可尝试直连；公共屏幕中继尚未启用'):'';
    if(!s.owner)byId('screenNetwork').textContent='自动画质优先保障人声与音乐；网络往返时间不等于画面总延迟。';
    this.syncSound();
  }
  leave(){this.engine?.close();this.engine=null;this.joined=false;this.state={owner:null};this.hasVideo=false;byId('screenVideo').srcObject=null;this.render();}
}
