import {ScreenShare} from './screen.js';

const el=id=>document.getElementById(id);

export class CameraView {
  constructor({send,displaySession,displaySelecting,audioPressure}) {
    Object.assign(this,{send,displaySession,displaySelecting,audioPressure});
    this.remote=new Map();
    this.pending=new Map();
    el('cameraStart').onclick=()=>{
      if(this.own?.stream||this.own?.selecting)this.own.stop();
      else if(!this.displaySelecting())void this.own?.select();
    };
    el('cameraDevice').onchange=()=>{if(this.own?.stream)void this.own.select(true);};
  }
  get session(){return this.own?.session;}
  get selecting(){return !!this.own?.selecting;}
  join(id){
    this.leave();
    this.id=id;
    this.own=this.makeEngine(id,{
      capture:options=>{
        const device=el('cameraDevice').value;
        return navigator.mediaDevices.getUserMedia({video:{...options.video,...(device?{deviceId:{exact:device}}:{})},audio:false});
      },
      otherSession:this.displaySession,
      onState:()=>this.render(),
      onVideo:stream=>{
        this.preview=stream;
        const video=el('cameraPreview');
        video.srcObject=stream;video.hidden=!stream;
        if(stream){
          void video.play().catch(()=>{});
          void this.refreshDevices();
        }
        this.render();
      },
      onStatus:message=>{el('cameraStatus').textContent=message.replace('正在共享画面；共享声音由独立开关控制','摄像头已开启');}
    });
    this.render();
  }
  makeEngine(owner,callbacks){
    const engine=new ScreenShare({
      selfId:this.id,camera:true,refreshIce:false,
      send:message=>this.send({...message,cameraOwner:owner}),...callbacks
    });
    engine.audioPressure=this.audioPressure;
    if(this.hello)engine.configure(this.hello);
    return engine;
  }
  receive(message){
    if(!this.own)return;
    if(message.type==='screen:hello'){
      this.hello=message;
      if(message.cameras){
        this.own.configure(message);
        for(const item of this.remote.values())item.engine.configure(message);
      }
      return;
    }
    if(message.type==='screen:cameras'){
      this.updateList(message.cameras);
      return;
    }
    if(message.cameraOwner===this.id)return this.own.receive(message);
    return this.remote.get(message.cameraOwner)?.engine.receive(message);
  }
  updateList(cameras){
    const ids=new Set(cameras.map(camera=>camera.owner));
    const own=cameras.find(camera=>camera.owner===this.id);
    if(own||this.own.state.owner)void this.own.receive(own||{type:'screen:state',owner:null});
    for(const [id,item] of this.remote)if(!ids.has(id)){
      item.engine.close();item.tile.remove();this.remote.delete(id);
    }
    if(!this.remote.has(this.pinned))this.pinned=null;
    for(const state of cameras){
      if(state.owner===this.id)continue;
      const item=this.remote.get(state.owner)||this.addTile(state.owner);
      item.name.textContent=state.name;
      item.select.setAttribute('aria-label','观看 '+state.name+' 的摄像头');
      item.state=state;
      void item.engine.receive(state);
      if(this.pinned===state.owner&&state.viewers.includes(this.id)&&item.focusSession!==state.session){
        item.focusSession=state.session;
        this.send({type:'screen:focus',cameraOwner:state.owner,session:state.session,featured:true});
      }
    }
    this.selectStreams();
  }
  addTile(owner){
    const tile=document.createElement('article'),name=document.createElement('strong'),video=document.createElement('video'),
      select=document.createElement('button'),full=document.createElement('button'),status=document.createElement('span'),stage=document.createElement('div');
    tile.className='cameraTile';name.className='cameraName';stage.className='cameraStage';
    video.autoplay=true;video.muted=true;video.playsInline=true;video.hidden=true;
    select.type=full.type='button';select.className='mini';select.textContent='观看';
    full.className='mini cameraFull';full.textContent='⛶';full.setAttribute('aria-label','全屏观看摄像头');full.hidden=true;
    status.className='small cameraTileStatus';
    const fullscreen=()=>{const request=document.fullscreenElement===stage?document.exitFullscreen():stage.requestFullscreen();void request.catch(()=>{});};
    full.onclick=fullscreen;video.ondblclick=fullscreen;
    select.onclick=()=>this.focus(owner);
    stage.append(video,full);tile.append(name,stage,status,select);el('cameraGallery').append(tile);
    const item={tile,name,select,status,video,full,engine:null,state:null};
    item.engine=this.makeEngine(owner,{
      onVideo:stream=>{
        video.srcObject=stream;video.hidden=!stream;full.hidden=!stream;
        if(stream)void video.play().catch(()=>{});
      },
      onMetrics:metric=>{
        if(item.engine.watching&&metric.width)
          status.textContent=metric.width+' × '+metric.height+' / '+metric.fps+' 帧';
      }
    });
    item.engine.watching=false;
    this.remote.set(owner,item);
    return item;
  }
  focus(owner){
    const item=this.remote.get(owner);
    if(!item||this.pinned===owner)return;
    this.pinned=owner;
    this.selectStreams();
    if(item.state.viewers.includes(this.id)){
      item.focusSession=item.state.session;
      this.send({type:'screen:focus',cameraOwner:owner,session:item.state.session,featured:true});
    }
  }
  selectStreams(){
    const count=this.displayVisible?3:4;
    const ids=[...this.remote.keys()];
    const selected=new Set((this.pinned?[this.pinned,...ids.filter(id=>id!==this.pinned)]:ids).slice(0,count));
    for(const [id,item] of this.remote)if(!selected.has(id)&&item.engine.watching)item.engine.setWatching(false);
    for(const [id,item] of this.remote){
      const watching=selected.has(id),budget=item.state?.budgets?.[this.id];
      if(watching&&!item.engine.watching)item.engine.setWatching(true);
      item.select.hidden=id===this.pinned;item.select.textContent=watching?'设为主画面':'观看';item.tile.classList.toggle('cameraPinned',id===this.pinned);
      item.status.textContent=!watching?'点击观看':item.state?.connected===false?'对方正在重连':budget===0?'带宽紧张 · 画面暂停':item.video.srcObject?'正在观看':'正在连接';
    }
  }
  setDisplayVisible(visible){
    if(this.displayVisible!==visible){this.displayVisible=visible;this.selectStreams();}
    this.render();
  }
  async refreshDevices(){
    try{
      const devices=(await navigator.mediaDevices.enumerateDevices()).filter(device=>device.kind==='videoinput');
      if(!this.own?.stream)return;
      const select=el('cameraDevice'),selected=this.own.stream.getVideoTracks()[0]?.getSettings().deviceId;
      select.replaceChildren(new Option('系统默认摄像头',''),...devices.map((device,index)=>new Option(device.label||'摄像头 '+(index+1),device.deviceId)));
      select.value=selected||'';if(select.selectedIndex<0)select.selectedIndex=0;
    }catch{}
  }
  render(){
    const supported=!!this.hello?.cameras,active=!!this.own?.stream,button=el('cameraStart');
    button.disabled=!supported||!navigator.mediaDevices?.getUserMedia||this.displaySelecting();
    button.textContent=this.selecting?'正在开启（点击取消）':active?'摄像头：开':'摄像头：关';
    button.setAttribute('aria-pressed',String(active));
    el('cameraDeviceWrap').hidden=!this.preview;
    if(!supported&&this.hello)el('cameraStatus').textContent='当前服务未启用摄像头。';
  }
  resume(){this.own?.resume();for(const item of this.remote.values())item.engine.resume();}
  leave(){
    this.own?.close();this.own=null;this.hello=null;this.preview=null;this.pinned=null;
    for(const item of this.remote.values()){item.engine.close();item.tile.remove();}
    this.remote.clear();
    el('cameraPreview').hidden=true;el('cameraPreview').srcObject=null;el('cameraStatus').textContent='';
    this.render();
  }
}
