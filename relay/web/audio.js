export class BrowserAudio{
  constructor(send,stats,status){this.send=send;this.onStats=stats;this.status=status;this.decoders=new Map();this.gains=new Map();this.capture=[null,null];this.micEpoch=0;this.shareEpoch=0;this.pressed=false;this.options={microphone:false,microphoneGain:100,speakerGain:100,talkMode:'free',talkKey:86,autoBuffer:true,memberVoice:{},memberMusic:{}};}
  async start(){
    if(!window.AudioDecoder||!window.AudioEncoder)throw new Error('当前浏览器缺少实时音频编码，请使用新版 Edge / Chrome 或 Windows 客户端');
    for(const channels of [1,2])if(!(await AudioDecoder.isConfigSupported({codec:'opus',sampleRate:48000,numberOfChannels:channels})).supported)throw new Error('浏览器不支持 Opus 解码，请使用 Windows 客户端');
    this.context=new AudioContext({sampleRate:48000,latencyHint:'interactive'});await this.context.resume();await this.context.audioWorklet.addModule('/room-worklet.js');
    this.mixer=new AudioWorkletNode(this.context,'voicebridge-room-mixer',{numberOfInputs:0,numberOfOutputs:1,outputChannelCount:[2]});this.mixer.connect(this.context.destination);this.noticeGain=this.context.createGain();this.noticeGain.connect(this.context.destination);this.mixer.port.onmessage=({data})=>this.onStats(data);this.configure(this.options);
  }
  configure(options){this.options={...this.options,...options};if(this.noticeGain)this.noticeGain.gain.value=this.options.speakerGain/100;this.mixer?.port.postMessage({type:'master',value:this.options.speakerGain/100});this.mixer?.port.postMessage({type:'auto',value:this.options.autoBuffer});
    for(const [id,value] of Object.entries(this.options.memberVoice||{}))this.setGain(id,0,value);for(const [id,value] of Object.entries(this.options.memberMusic||{}))this.setGain(id,1,value);this.updateGate();this.capture[1]?.node.port.postMessage({enabled:true,gain:(this.options.musicGain??100)/100,epoch:this.shareEpoch});}
  notify(joined){
    if(!this.context||!this.noticeGain)return;const start=this.context.currentTime;
    for(const [n,frequency] of [660,joined?880:440].entries()){const oscillator=this.context.createOscillator(),gain=this.context.createGain(),at=start+n*.09;oscillator.frequency.value=frequency;gain.gain.setValueAtTime(0,at);gain.gain.linearRampToValueAtTime(.055,at+.01);gain.gain.linearRampToValueAtTime(0,at+.09);oscillator.connect(gain).connect(this.noticeGain);oscillator.start(at);oscillator.stop(at+.09);oscillator.onended=()=>{oscillator.disconnect();gain.disconnect();};}
  }
  setGain(id,kind,percent){const key=id+':'+kind,value=percent/100;this.gains.set(key,value);this.mixer?.port.postMessage({type:'gain',key,value});}
  receive(frame){
    const key=frame.id+':'+frame.kind;let item=this.decoders.get(key);if(!item){
      item={last:-1,decoder:null};const channels=frame.kind===1?2:1;
      const decoder=new AudioDecoder({output:audio=>{try{if(!this.mixer)return;const planes=[];for(let c=0;c<audio.numberOfChannels;c++){const plane=new Float32Array(audio.numberOfFrames);audio.copyTo(plane,{planeIndex:c,format:'f32-planar'});planes.push(plane);}const samples=new Float32Array(audio.numberOfFrames*2);let peak=0;for(let n=0;n<audio.numberOfFrames;n++){samples[n*2]=planes[0][n];samples[n*2+1]=planes[Math.min(1,planes.length-1)][n];peak=Math.max(peak,Math.abs(samples[n*2]),Math.abs(samples[n*2+1]));}this.mixer.port.postMessage({type:'packet',key,samples:samples.buffer,arrival:performance.now(),gain:this.gains.get(key)??1,peak},[samples.buffer]);}finally{audio.close();}},error:()=>{if(this.decoders.get(key)?.decoder===decoder)this.decoders.delete(key);this.status('有一路音频已重置，正在等待下一帧');}});
      decoder.configure({codec:'opus',sampleRate:48000,numberOfChannels:channels});item.decoder=decoder;this.decoders.set(key,item);
    }
    if(frame.sequence<=item.last||item.decoder.decodeQueueSize>8)return;item.last=frame.sequence;item.decoder.decode(new EncodedAudioChunk({type:'key',timestamp:frame.sequence*20000,duration:20000,data:frame.opus}));
  }
  remove(id){this.mixer?.port.postMessage({type:'remove',id});for(const [key,item] of this.decoders)if(key.startsWith(id+':')){if(item.decoder.state!=='closed')item.decoder.close();this.decoders.delete(key);}}
  async inputs(){return (await navigator.mediaDevices.enumerateDevices()).filter(d=>d.kind==='audioinput'&&d.deviceId!=='communications');}
  async microphone(enabled,deviceId=''){
    this.stopCapture(0);if(!enabled){this.options.microphone=false;return;}
    const epoch=++this.micEpoch;const stream=await navigator.mediaDevices.getUserMedia({audio:{...(deviceId?{deviceId:{exact:deviceId}}:{}),echoCancellation:true,noiseSuppression:true,autoGainControl:false,channelCount:1},video:false});
    if(epoch!==this.micEpoch||!this.context){stream.getTracks().forEach(t=>t.stop());return;}
    if(stream.getAudioTracks().some(t=>/cable|voicemeeter|stereo mix|立体声混音/i.test(t.label))){stream.getTracks().forEach(t=>t.stop());throw new Error('选到了虚拟输入，请选择真实麦克风');}
    this.options.microphone=true;await this.addCapture(0,stream,epoch);this.updateGate();
  }
  async shareTab(){
    if(!navigator.mediaDevices.getDisplayMedia)throw new Error('此浏览器不支持标签页音频共享，请用 Windows 客户端');
    this.stopCapture(1);const epoch=++this.shareEpoch;
    const stream=await navigator.mediaDevices.getDisplayMedia({video:{displaySurface:'browser'},audio:{suppressLocalAudioPlayback:false},selfBrowserSurface:'exclude',systemAudio:'exclude',preferCurrentTab:false});
    const track=stream.getVideoTracks()[0],handle=track.getCaptureHandle?.();
    if(track.getSettings().displaySurface!=='browser'||/voicebridge/i.test(track.label)||handle?.origin===location.origin&&handle.handle==='voicebridge-room'||stream.getAudioTracks().length===0){stream.getTracks().forEach(t=>t.stop());throw new Error('请选择其他浏览器标签页，并勾选共享标签页音频；不能选择本房间或整个系统声音');}
    if(epoch!==this.shareEpoch||!this.context){stream.getTracks().forEach(t=>t.stop());return;}await this.addCapture(1,stream,epoch);track.onended=()=>{this.stopCapture(1);this.status('标签页音乐共享已停止');};
  }
  async addCapture(kind,stream,epoch){
    const channels=kind?2:1,config={codec:'opus',sampleRate:48000,numberOfChannels:channels,bitrate:kind?256000:48000,opus:{format:'opus',frameDuration:20000}};
    const valid=()=>epoch===(kind?this.shareEpoch:this.micEpoch)&&!!this.context;
    const cancel=()=>stream.getTracks().forEach(t=>t.stop());
    try{
    if(!(await AudioEncoder.isConfigSupported(config)).supported)throw new Error('浏览器不支持实时编码，请使用 Windows 客户端');
    if(!valid()){cancel();return;}await this.context.resume();if(!valid()){cancel();return;}const source=this.context.createMediaStreamSource(stream),node=new AudioWorkletNode(this.context,'voicebridge-room-capture',{numberOfInputs:1,numberOfOutputs:1,outputChannelCount:[channels],channelCount:channels,channelCountMode:'explicit',processorOptions:{channels}});
    const pending=new Map();let timestamp=0;const encoder=new AudioEncoder({output:chunk=>{const frameEpoch=pending.get(chunk.timestamp);pending.delete(chunk.timestamp);if(frameEpoch!==(kind?this.shareEpoch:this.micEpoch)||!this.capture[kind]||kind===0&&!this.gate())return;const opus=new Uint8Array(chunk.byteLength);chunk.copyTo(opus);this.send(kind,opus,frameEpoch);},error:()=>this.status('发送编码失败，请关闭后重新开启')});encoder.configure(config);
    this.capture[kind]={stream,source,node,encoder,pending};node.port.onmessage=({data})=>{if(data.epoch!==(kind?this.shareEpoch:this.micEpoch)||!this.capture[kind]||encoder.encodeQueueSize>4)return;const audio=new AudioData({format:'f32',sampleRate:48000,numberOfFrames:960,numberOfChannels:channels,timestamp,data:data.pcm});pending.set(timestamp,data.epoch);timestamp+=20000;encoder.encode(audio);audio.close();};
    source.connect(node).connect(this.context.destination);node.port.postMessage({enabled:kind===1||this.gate(),gain:kind?(this.options.musicGain??100)/100:this.options.microphoneGain/100,epoch});
    for(const track of stream.getAudioTracks())track.onended=()=>{this.stopCapture(kind);this.status(kind?'共享已停止':'麦克风已断开');};
    }catch(error){cancel();this.stopCapture(kind);throw error;}
  }
  gate(){return this.options.microphone&&(this.options.talkMode==='free'||this.pressed);}
  keyDown(code){if(code===this.options.talkKey&&!this.pressed){this.pressed=true;this.micEpoch++;this.updateGate();}}
  keyUp(){if(this.pressed){this.pressed=false;this.micEpoch++;this.updateGate();}}
  updateGate(){const capture=this.capture[0];if(capture)capture.node.port.postMessage({enabled:this.gate(),gain:this.options.microphoneGain/100,epoch:this.micEpoch});}
  stopCapture(kind){if(kind===0){this.micEpoch++;this.options.microphone=false;this.pressed=false;}else this.shareEpoch++;const capture=this.capture[kind];this.capture[kind]=null;if(!capture)return;capture.source.disconnect();capture.node.disconnect();if(capture.encoder.state!=='closed')capture.encoder.close();for(const track of capture.stream.getTracks()){track.onended=null;track.stop();}}
  async stop(){this.stopCapture(0);this.stopCapture(1);for(const item of this.decoders.values())if(item.decoder.state!=='closed')item.decoder.close();this.decoders.clear();this.mixer?.disconnect();this.mixer=null;const old=this.context;if(old?.state==='running')this.notify(false);this.context=null;if(old){if(old.state==='running')await new Promise(resolve=>setTimeout(resolve,200));await old.close().catch(()=>{});}}
}
