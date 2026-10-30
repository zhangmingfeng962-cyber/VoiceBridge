export const screenProfiles=Object.freeze({
  smooth:{width:1280,height:720,fps:30,bitrate:1400000},
  balanced:{width:1920,height:1080,fps:30,bitrate:3000000},
  clear:{width:1920,height:1080,fps:60,bitrate:4500000}
});
const stopTracks=stream=>stream?.getTracks().forEach(track=>track.stop());
const errorText=error=>error?.name==='NotAllowedError'?'已取消选择或未允许屏幕共享':error?.name==='NotFoundError'?'没有可共享的窗口或显示器':error?.name==='NotReadableError'?'所选窗口暂时无法捕获，请重新选择':error?.message||'屏幕连接失败';

// Video only. Program sound deliberately remains in the existing encrypted music lane.
export class ScreenShare{
  constructor({selfId,send,onState=()=>{},onVideo=()=>{},onStatus=()=>{},onMetrics=()=>{},onStopped=()=>{},capture,createPeer,makeStream}={}){
    Object.assign(this,{selfId,send,onState,onVideo,onStatus,onMetrics,onStopped});
    this.capture=capture||((options)=>navigator.mediaDevices.getDisplayMedia(options));
    this.createPeer=createPeer||((options)=>new RTCPeerConnection(options));
    this.makeStream=makeStream||(track=>new MediaStream([track]));
    this.state={owner:null};this.stream=null;this.session=null;this.links=new Map();this.mode='clear';this.auto=true;this.watching=true;this.selecting=false;this.closed=false;this.captureEpoch=0;this.pending=null;this.iceServers=[];this.relayAvailable=false;this.supported=false;this.expiry=0;this.watchRequest=null;this.retryCount=0;this.incoming=Promise.resolve();
  }
  emit(){this.onState({...this.state,self:this.state.owner===this.selfId,selecting:this.selecting,watching:this.watching,mode:this.state.owner===this.selfId?this.mode:this.state.mode||this.mode,auto:this.auto,supported:this.supported,relayAvailable:this.relayAvailable});}
  configure(message){
    const renewed=!!this.expiry&&message.expires>this.expiry;
    this.iceServers=message.iceServers||[];this.expiry=message.expires;this.relayAvailable=!!message.relayAvailable;this.supported=message.version===1;
    if(!this.refreshTimer){this.refreshTimer=setInterval(()=>this.send({type:'screen:ice'}),240000);this.refreshTimer.unref?.();}
    if(renewed)for(const item of this.links.values()){
      try{item.pc.setConfiguration({...item.pc.getConfiguration(),iceServers:this.iceServers});}catch{}
      if(item.owner)this.restartIce(item);
      else this.send({type:'screen:refresh',session:item.session,link:item.link,to:item.peer});
    }
    this.emit();
  }
  receive(message){
    this.incoming=this.incoming.then(()=>this.handle(message)).catch(error=>this.onStatus(errorText(error)));return this.incoming;
  }
  async handle(message){
    if(this.closed)return;
    if(message.type==='screen:hello'){this.configure(message);return;}
    if(message.type==='screen:claimed'){
      if(this.pending?.id===message.requestId){this.session=message.session;clearTimeout(this.pending.timer);this.pending.resolve(message);this.pending=null;}
      else this.send({type:'screen:stop',session:message.session});
      return;
    }
    if(message.type==='screen:error'){
      const reason=message.code==='busy'?'房间已有成员共享，请等对方结束后再试':message.code==='stale'?'共享已经结束，请重新发起':'屏幕共享请求未通过，请重新进入房间';
      if(this.pending?.id===message.requestId){clearTimeout(this.pending.timer);this.pending.reject(Error(reason));this.pending=null;}else if(message.code!=='stale')this.onStatus(reason);
      return;
    }
    if(message.type==='screen:state'){
      const changed=this.state.session!==message.session,wasOwner=this.state.owner===this.selfId;
      if(changed){this.closeLinks();this.watchRequest=null;this.retryCount=0;if(message.owner!==this.selfId)this.onVideo(null,false);else if(this.stream)this.onVideo(this.stream,true);if(wasOwner&&message.owner!==this.selfId)this.releaseCapture();}
      this.state=message;
      if(message.owner===this.selfId){this.session=message.session;for(const item of this.links.values())void this.limit(item);}
      else if(message.owner&&this.watching&&this.supported&&!this.watchRequest)this.watch();
      this.emit();return;
    }
    if(message.session!==this.state.session&&message.session!==this.session)return;
    if(message.type==='screen:watching'){
      this.closeLinks();this.watchRequest=message.session;
      this.connection(message.owner,message.link,false);return;
    }
    if(message.type==='screen:viewer'&&this.state.owner===this.selfId){
      if(!message.joined){this.closeLink(message.viewer);return;}
      this.closeLink(message.viewer);const item=this.connection(message.viewer,message.link,true);
      const track=this.stream?.getVideoTracks()[0];if(!track){this.stop();return;}
      const transceiver=item.pc.addTransceiver('video',{direction:'sendonly',streams:[this.stream]});item.sender=transceiver.sender;
      await item.sender.replaceTrack(this.state.paused?null:track);
      if(this.links.get(item.peer)!==item)return;
      await item.pc.setLocalDescription(await item.pc.createOffer());
      if(this.links.get(item.peer)===item)this.signal(item,{description:item.pc.localDescription.toJSON()});
      await this.limit(item);return;
    }
    if(message.type==='screen:refresh'){const item=this.links.get(message.from);if(item?.owner&&item.link===message.link)this.restartIce(item);return;}
    if(message.type==='screen:signal'){
      const item=this.links.get(message.from);if(!item||item.link!==message.link)return;
      if(message.description){
        if(message.description.type!==(item.owner?'answer':'offer'))return;
        await item.pc.setRemoteDescription(message.description);
        for(const candidate of item.candidates.splice(0))await this.addCandidate(item,candidate);
        if(!item.owner){await item.pc.setLocalDescription(await item.pc.createAnswer());if(this.links.get(item.peer)===item)this.signal(item,{description:item.pc.localDescription.toJSON()});}
      }else for(const candidate of message.candidates||[]){if(item.pc.remoteDescription&&item.pc.signalingState!=='have-local-offer')await this.addCandidate(item,candidate);else if(item.candidates.length<96)item.candidates.push(candidate);}
    }
  }
  async select(replace=false){
    if(this.closed||this.selecting||!this.supported)return;
    if(this.state.owner&&this.state.owner!==this.selfId){this.onStatus('房间已有成员共享，请等对方结束后再试');return;}
    const request=++this.captureEpoch;this.selecting=true;this.emit();let selected;const replaced=[];
    try{
      const profile=screenProfiles[this.mode];
      // Call before the first await so the browser receives the user's original click activation.
      selected=await this.capture({video:{width:{ideal:profile.width,max:1920},height:{ideal:profile.height,max:1080},frameRate:{ideal:profile.fps,max:60}},audio:false,selfBrowserSurface:'exclude',systemAudio:'exclude',surfaceSwitching:'include',preferCurrentTab:false});
      if(this.closed||request!==this.captureEpoch){stopTracks(selected);return;}
      selected.getAudioTracks().forEach(track=>{track.stop();selected.removeTrack?.(track);});
      const track=selected.getVideoTracks()[0];if(!track||track.readyState==='ended')throw Error('没有可用的画面轨道');
      const surface=track.getSettings().displaySurface||'monitor';
      if(!['window','monitor','browser'].includes(surface))throw Error('不支持所选画面来源');
      if(replace&&this.stream&&this.state.owner===this.selfId){
        track.enabled=!this.state.paused;
        for(const item of this.links.values())if(item.sender){await item.sender.replaceTrack(this.state.paused?null:track);replaced.push(item);}
        if(this.closed||request!==this.captureEpoch){stopTracks(selected);return;}
        const old=this.stream;this.stream=selected;stopTracks(old);
      }else{
        this.stream=selected;
        const requestId=globalThis.crypto.randomUUID().replaceAll('-','').slice(0,32);
        await new Promise((resolve,reject)=>{const timer=setTimeout(()=>{this.pending=null;reject(Error('请求共享超时，请重试'));},8000);this.pending={id:requestId,resolve,reject,timer};this.send({type:'screen:claim',requestId,mode:this.mode,surface});});
        if(this.closed||request!==this.captureEpoch){stopTracks(selected);if(this.session)this.send({type:'screen:stop',session:this.session});return;}
      }
      track.onended=()=>{if(this.stream===selected)this.stop();};
      track.onmute=()=>{if(this.stream===selected&&this.state.owner===this.selfId&&!this.state.paused){void this.pause(true);this.onStatus('所选窗口暂不可用，画面已暂停，可恢复或更换来源');}};
      await this.constraints();if(this.closed||request!==this.captureEpoch||this.stream!==selected){stopTracks(selected);return;}this.onVideo(selected,true);
      if(replace)this.update();this.onStatus('正在共享画面；共享声音由独立开关控制');
    }catch(error){
      // A rejected switch must not leave some viewers bound to a stopped replacement track.
      if(replace&&this.stream&&this.stream!==selected){const oldTrack=this.stream.getVideoTracks()[0];for(const item of replaced)try{await item.sender.replaceTrack(this.state.paused?null:oldTrack);}catch{this.closeLink(item.peer);}}
      stopTracks(selected);
      if(this.stream===selected){this.stream=null;if(this.session)this.send({type:'screen:stop',session:this.session});this.session=null;this.onVideo(null,true);this.onStopped();}
      if(request===this.captureEpoch&&!this.closed)this.onStatus(errorText(error));
    }finally{if(request===this.captureEpoch){this.selecting=false;this.emit();}}
  }
  update(){if(this.session&&this.stream)this.send({type:'screen:update',session:this.session,paused:!!this.state.paused,mode:this.mode,surface:this.stream.getVideoTracks()[0]?.getSettings().displaySurface||'monitor'});}
  async pause(value=!this.state.paused){
    if(this.state.owner!==this.selfId||!this.stream)return;
    const session=this.session,track=this.stream.getVideoTracks()[0];
    if(value){track.enabled=false;this.state={...this.state,paused:true};this.update();this.emit();}
    try{
      for(const item of this.links.values())if(item.sender)await item.sender.replaceTrack(value?null:track);
      if(this.session!==session||!this.stream)return;
      if(!value){track.enabled=true;this.state={...this.state,paused:false};this.update();this.emit();}
    }catch{track.enabled=false;this.state={...this.state,paused:true};this.update();this.emit();this.onStatus('画面保持暂停，请更换来源或重新共享');}
  }
  async constraints(){
    const track=this.stream?.getVideoTracks()[0];if(!track)return;const p=screenProfiles[this.mode];track.contentHint=this.mode==='clear'?'detail':'motion';
    try{await track.applyConstraints({width:{ideal:p.width,max:p.width},height:{ideal:p.height,max:p.height},frameRate:{ideal:p.fps,max:p.fps}});}catch{this.onStatus('设备未接受全部画质限制，仍会限制发送码率');}
  }
  async addCandidate(item,candidate){
    const sdp=item.pc.remoteDescription?.sdp||'',ufrag=/^a=ice-ufrag:(.+)$/m.exec(sdp)?.[1]?.trim();
    if(ufrag&&candidate.usernameFragment&&candidate.usernameFragment!==ufrag){if(item.candidates.length<96)item.candidates.push(candidate);return;}
    try{await item.pc.addIceCandidate(candidate);}catch(error){if(error.name!=='OperationError')throw error;}
  }
  restartIce(item){
    if(this.closed||!item.owner||this.links.get(item.peer)!==item||item.renewTimer)return;
    item.renewTimer=setTimeout(async()=>{
      item.renewTimer=null;if(this.closed||this.links.get(item.peer)!==item)return;
      if(item.pc.signalingState!=='stable'){this.restartIce(item);return;}
      try{await item.pc.setLocalDescription(await item.pc.createOffer({iceRestart:true}));if(this.links.get(item.peer)===item)this.signal(item,{description:item.pc.localDescription.toJSON()});}
      catch{this.onStatus('中继连接续期暂未完成，原画面继续传送');}
    },500);
  }
  resume(){
    if(this.closed)return;this.send({type:'screen:ice'});
    for(const item of this.links.values())if(item.owner)this.restartIce(item);
    if(this.state.owner&&this.state.owner!==this.selfId&&this.watching&&![...this.links.values()].some(x=>x.pc.connectionState==='connected'))this.watch(true);
  }
  async quality(mode,auto=this.auto){if(!screenProfiles[mode])return;this.mode=mode;this.auto=auto;await this.constraints();for(const item of this.links.values()){item.factor=1;void this.limit(item);}this.update();this.emit();}
  watch(restart=false){
    if(!this.state.owner||this.state.owner===this.selfId||!this.watching)return;
    this.watchRequest=this.state.session;if(restart)this.closeLinks();
    this.send({type:'screen:watch',session:this.state.session,restart});
  }
  setWatching(value){
    this.watching=value;if(value){this.retryCount=0;this.watch(true);}else{if(this.state.session)this.send({type:'screen:unwatch',session:this.state.session});this.watchRequest=null;this.closeLinks();this.onVideo(null,false);}this.emit();
  }
  signal(item,data){if(!this.closed&&this.links.get(item.peer)===item)this.send({type:'screen:signal',session:item.session,link:item.link,to:item.peer,...data});}
  connection(peer,link,owner){
    const pc=this.createPeer({iceServers:this.iceServers,bundlePolicy:'max-bundle'});
    const item={peer,link,pc,owner,session:this.state.session||this.session,candidates:[],outgoing:[],factor:1,stable:0,work:Promise.resolve(),last:null};this.links.set(peer,item);
    pc.onicecandidate=event=>{
      if(!event.candidate||this.links.get(peer)!==item)return;
      if(item.outgoing.length<96)item.outgoing.push(event.candidate.toJSON());
      if(!item.flush)item.flush=setTimeout(()=>{item.flush=null;while(item.outgoing.length)this.signal(item,{candidates:item.outgoing.splice(0,16)});},100);
    };
    pc.ontrack=event=>{if(event.track.kind!=='video'){event.track.stop();return;}if(this.links.get(peer)===item)this.onVideo(this.makeStream(event.track),false);};
    pc.onconnectionstatechange=()=>{
      if(this.links.get(peer)!==item)return;
      if(pc.connectionState==='connected'){clearTimeout(item.deadline);clearTimeout(item.retry);this.retryCount=0;this.onStatus('屏幕画面已连接');}
      if(['failed','disconnected'].includes(pc.connectionState)&&!owner&&!item.retry)item.retry=setTimeout(()=>this.retry(item),pc.connectionState==='failed'?800:5000);
    };
    item.deadline=setTimeout(()=>{if(pc.connectionState!=='connected'&&!owner)this.retry(item);},14000);
    item.stats=setInterval(()=>void this.statistics(item),2500);item.stats.unref?.();return item;
  }
  retry(item){
    if(this.closed||!this.watching||this.links.get(item.peer)!==item)return;
    if(++this.retryCount>3){this.closeLinks();this.onVideo(null,false);this.onStatus(this.relayAvailable?'屏幕连接失败，可点击“重连画面”；语音和音乐继续运行':'直连未成功，服务器尚未配置屏幕中继；语音和音乐继续运行');return;}
    this.onStatus(this.relayAvailable?'正在重连画面，必要时通过服务器中继':'正在重试屏幕直连');this.watch(true);
  }
  limit(item){
    item.work=item.work.then(async()=>{
      if(!item.sender||this.links.get(item.peer)!==item)return;
      const profile=screenProfiles[this.mode],ceiling=Math.min(profile.bitrate,this.state.budgets?.[item.peer]||this.state.maxBitrate||profile.bitrate),bitrate=Math.max(Math.min(32000,ceiling),Math.floor(ceiling*(this.auto?item.factor:1)));
      const fps=Math.min(profile.fps,bitrate>=3000000?60:bitrate>=2000000?45:bitrate>=1000000?30:bitrate>=500000?20:bitrate>=200000?12:6);
      const params=item.sender.getParameters();if(!params.encodings?.length)return;
      Object.assign(params.encodings[0],{maxBitrate:bitrate,maxFramerate:fps,scaleResolutionDownBy:1,priority:'low'});
      params.degradationPreference='maintain-resolution';
      try{await item.sender.setParameters(params);}catch{delete params.encodings[0].priority;delete params.degradationPreference;await item.sender.setParameters(params);}
      item.bitrate=bitrate;item.targetFps=fps;
    }).catch(()=>this.onStatus('浏览器无法应用部分画质设置，网络拥塞控制仍由 WebRTC 执行'));return item.work;
  }
  async statistics(item){
    if(this.links.get(item.peer)!==item)return;
    try{
      const reports=await item.pc.getStats();let rtt=0,loss=0,available=0,video,relay=false,relayHops=0;
      const transports=[...reports.values()].filter(x=>x.type==='transport'&&x.selectedCandidatePairId),selected=transports.map(x=>reports.get(x.selectedCandidatePairId)).find(Boolean)||[...reports.values()].find(x=>x.type==='candidate-pair'&&x.state==='succeeded'&&x.nominated);
      if(selected){rtt=selected.currentRoundTripTime||0;available=selected.availableOutgoingBitrate||0;relayHops=Number(reports.get(selected.localCandidateId)?.candidateType==='relay')+Number(reports.get(selected.remoteCandidateId)?.candidateType==='relay');relay=relayHops>0;}
      for(const stat of reports.values()){
        if(stat.type==='remote-inbound-rtp'&&stat.kind==='video')loss=Math.max(loss,stat.fractionLost||0);
        if((stat.type==='inbound-rtp'||stat.type==='outbound-rtp')&&stat.kind==='video')video=stat;
      }
      const pressure=this.audioPressure?.()||false;
      if(item.owner&&this.auto){
        // A low bandwidth estimate can also mean a static screen needs few bits.
        // Only use it as congestion evidence when the encoder reports bandwidth limitation.
        if(rtt>.3||loss>.04||pressure||video?.qualityLimitationReason==='bandwidth'&&available>0&&available<(item.bitrate||0)*.75){item.factor=Math.max(.15,item.factor*.8);item.stable=0;}
        else if(++item.stable>=6){item.factor=Math.min(1,item.factor+.08);item.stable=0;}
        await this.limit(item);
      }
      if(item.route!==relayHops&&video&&selected){item.route=relayHops;this.send({type:'screen:route',session:item.session,link:item.link,to:item.peer,relay,relayHops});}
      this.onMetrics({peer:item.peer,owner:item.owner,rttMs:Math.round(rtt*1000),relay,bitrate:item.bitrate||0,width:video?.frameWidth||0,height:video?.frameHeight||0,fps:Math.round(video?.framesPerSecond||0),targetFps:item.targetFps,limited:video?.qualityLimitationReason||'',paused:!!this.state.paused});
    }catch{} // Closing a peer during getStats is a normal source-change/leave race.
  }
  closeLink(peer){const item=this.links.get(peer);if(!item)return;this.links.delete(peer);clearTimeout(item.flush);clearTimeout(item.deadline);clearTimeout(item.retry);clearTimeout(item.renewTimer);clearInterval(item.stats);item.pc.ontrack=item.pc.onicecandidate=item.pc.onconnectionstatechange=null;item.pc.close();}
  closeLinks(){for(const peer of [...this.links.keys()])this.closeLink(peer);}
  releaseCapture(){const old=this.stream;this.stream=null;this.session=null;stopTracks(old);this.onVideo(null,true);this.onStopped();}
  stop(){
    this.captureEpoch++;this.selecting=false;
    if(this.pending){clearTimeout(this.pending.timer);this.pending.reject(Error('已结束共享'));this.pending=null;}
    if(this.session)this.send({type:'screen:stop',session:this.session});this.releaseCapture();this.closeLinks();this.emit();
  }
  close(){if(this.closed)return;this.stop();this.closed=true;clearInterval(this.refreshTimer);this.onVideo(null,false);}
}
