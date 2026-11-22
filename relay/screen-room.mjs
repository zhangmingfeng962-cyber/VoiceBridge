import {createHmac,randomBytes} from 'node:crypto';

const id=()=>randomBytes(16).toString('hex');
const modes=new Set(['smooth','balanced','clear']);
const surfaces=new Set(['window','monitor','browser']);
const urls=value=>String(value||'').split(',').map(x=>x.trim()).filter(Boolean);

export function screenConfiguration(env=process.env){
  const stun=urls(env.STUN_URLS),turn=urls(env.TURN_URLS),secret=env.TURN_SHARED_SECRET||'';
  if(stun.some(x=>!/^stun:[a-z0-9.:-]+$/i.test(x))||turn.some(x=>!/^turns?:[a-z0-9.:-]+(?:\?transport=(?:udp|tcp))?$/i.test(x)))throw Error('Invalid screen ICE server URLs');
  if(!!turn.length!==!!secret||secret&&secret.length<32)throw Error('Configure TURN_URLS and a private TURN_SHARED_SECRET together (32+ characters)');
  const budget=Number(env.SCREEN_VIDEO_BUDGET_BPS||5000000);
  if(!Number.isSafeInteger(budget)||budget<128000||budget>8000000)throw Error('SCREEN_VIDEO_BUDGET_BPS must be between 128000 and 8000000');
  return {stun,turn,secret,budget};
}

export function screenIce(config,room,member,now=Date.now()){
  const iceServers=config.stun.length?[{urls:config.stun}]:[];
  const expires=Math.floor(now/1000)+600;
  if(config.turn.length){
    const username=`${expires}:${room}:${member}`;
    iceServers.push({urls:config.turn,username,credential:createHmac('sha1',config.secret).update(username).digest('base64')});
  }
  return {iceServers,expires,relayAvailable:!!config.turn.length};
}

// Only an authenticated browser member or the current paired control page is attached.
// Native audio sockets never receive or relay video, SDP or ICE credentials.
export class ScreenRooms{
  constructor(send,config){this.send=send;this.config=config;this.clients=new Map();this.shares=new Map();this.cameras=new Map();}
  emit(client,message){if(client)this.send(client.socket,message);}
  media(client,share,message){
    if(!share.cameraOwner)this.emit(client,message);
    else if(client?.videoVersion===2)this.emit(client,{...message,cameraOwner:share.cameraOwner});
  }
  budgets(){
    const total=Math.floor(this.config.budget*.8),count=shares=>{
      let legs=0;
      for(const share of shares)for(const viewer of share.viewers.keys())legs+=share.routes.get(viewer)??2;
      return legs;
    };
    const screenLegs=count(this.shares.values()),cameraLegs=count(this.cameras.values());
    const cameraPool=cameraLegs?(screenLegs?Math.floor(total*.35):total):0;
    const allocations=new Map(),minimum=90000;
    const links=[...this.cameras.values()].flatMap(share=>[...share.viewers.keys()]
      .map((viewer,order)=>({share,viewer,order,hops:share.routes.get(viewer)??2})).filter(link=>link.hops));
    links.sort((a,b)=>Number(b.share.featured.has(b.viewer))-Number(a.share.featured.has(a.viewer))||a.order-b.order);
    let remaining=cameraPool;
    for(const link of links)if(remaining>=minimum*link.hops){
      allocations.set(link.share.viewers.get(link.viewer),minimum);
      remaining-=minimum*link.hops;
    }
    const admitted=links.filter(link=>allocations.has(link.share.viewers.get(link.viewer)));
    const extra=Math.floor(remaining/Math.max(1,admitted.reduce((sum,link)=>sum+link.hops,0)));
    for(const key of allocations.keys())allocations.set(key,minimum+extra);
    return {screen:Math.floor((total-cameraPool)/Math.max(1,screenLegs)),camera:allocations};
  }
  shareState(share,budget=this.budgets()){
    if(!share)return {type:'screen:state',owner:null};
    const client=this.clients.get(share.owner),budgets={};
    const directLimit=share.cameraOwner?Math.floor(1200000/Math.max(1,share.viewers.size)):4500000;
    for(const [viewer,link] of share.viewers){
      const relayLimit=share.cameraOwner?budget.camera.get(link)??0:budget.screen;
      budgets[viewer]=share.routes.get(viewer)===0?directLimit:Math.min(directLimit,relayLimit);
    }
    return {type:'screen:state',owner:share.owner,name:client?.peer.name||'成员',connected:!!client&&!client.suspended,session:share.session,paused:share.paused,mode:share.mode,surface:share.surface,viewers:[...share.viewers.keys()],budgets,maxBitrate:Math.min(directLimit,...Object.values(budgets))};
  }
  state(room){return this.shareState(this.shares.get(room));}
  notify(room){
    const budget=this.budgets(),screen=this.shareState(this.shares.get(room),budget);
    const cameras=[...this.cameras.values()].filter(share=>share.room===room).map(share=>this.shareState(share,budget));
    for(const client of this.clients.values())if(client.peer.room===room){
      this.emit(client,screen);
      if(client.videoVersion===2)this.emit(client,{type:'screen:cameras',cameras});
    }
  }
  notifyAll(){for(const room of new Set([...this.clients.values()].map(c=>c.peer.room)))this.notify(room);}
  attach(peer,socket,preserve=false){
    if(!preserve)this.detach(peer.id);
    const client={peer,socket,start:Date.now(),count:0,suspended:false,videoVersion:socket.videoVersion};this.clients.set(peer.id,client);
    this.emit(client,{type:'screen:hello',version:1,cameras:client.videoVersion===2,...screenIce(this.config,peer.room,peer.id)});
    if(preserve)this.notifyAll();else this.notify(peer.room);
  }
  suspend(member,socket){const client=this.clients.get(member);if(client?.socket===socket){client.suspended=true;this.notifyAll();}}
  detach(member,socket){
    const client=this.clients.get(member);if(!client||socket&&client.socket!==socket)return;
    this.clients.delete(member);
    for(const share of [this.shares.get(client.peer.room),...this.cameras.values()]){
      if(!share||share.room!==client.peer.room)continue;
      if(share.owner===member)this.removeShare(share);
      else if(share.viewers.delete(member)){
        share.routes.delete(member);share.featured.delete(member);
        this.media(this.clients.get(share.owner),share,{type:'screen:viewer',session:share.session,viewer:member,joined:false});
      }
    }
    this.notifyAll();
  }
  removeShare(share){if(share.cameraOwner)this.cameras.delete(share.cameraOwner);else this.shares.delete(share.room);}
  dropRoom(room){
    this.shares.delete(room);
    for(const [member,share] of this.cameras)if(share.room===room)this.cameras.delete(member);
    for(const [member,client] of this.clients)if(client.peer.room===room)this.clients.delete(member);
    this.notifyAll();
  }
  handle(peer,socket,message){
    const client=this.clients.get(peer?.id);
    if(!client||client.socket!==socket)return;
    const now=Date.now();if(now-client.start>=1000){client.start=now;client.count=0;}
    if(++client.count>120)return; // Independent budget: ICE must never exhaust the voice control rate.
    const cameraOwner=message.cameraOwner;
    if(cameraOwner!==undefined&&(client.videoVersion!==2||!(/^[a-f0-9]{32}$/.test(cameraOwner))))return;
    const reject=code=>this.emit(client,{type:'screen:error',code,requestId:typeof message.requestId==='string'?message.requestId.slice(0,32):undefined,...(cameraOwner?{cameraOwner}:{})});
    const room=peer.room,display=this.shares.get(room),share=cameraOwner?this.cameras.get(cameraOwner):display;
    if(share&&share.room!==room){reject('forbidden');return;}
    if(message.type==='screen:ice'){this.emit(client,{type:'screen:hello',version:1,cameras:client.videoVersion===2,...screenIce(this.config,room,peer.id)});return;}
    if(message.type==='screen:claim'){
      const surfaceOk=cameraOwner?cameraOwner===peer.id&&message.surface==='camera':surfaces.has(message.surface);
      if(typeof message.requestId!=='string'||!/^[a-z0-9-]{1,32}$/i.test(message.requestId)||!modes.has(message.mode)||!surfaceOk){reject('invalid');return;}
      if(share){reject('busy');return;}
      const old=cameraOwner?(display?.owner===peer.id?display:null):this.cameras.get(peer.id);
      if(old&&old.session!==message.replaceSession){reject('busy');return;}
      const next={room,owner:peer.id,cameraOwner,session:id(),paused:false,mode:message.mode,surface:message.surface,viewers:new Map(),routes:new Map(),featured:new Set()};
      if(old)this.removeShare(old);
      if(cameraOwner)this.cameras.set(cameraOwner,next);else this.shares.set(room,next);
      this.media(client,next,{type:'screen:claimed',requestId:message.requestId,session:next.session});this.notifyAll();return;
    }
    if(!share||message.session!==share.session){reject('stale');return;}
    if(message.type==='screen:watch'){
      if(peer.id===share.owner){reject('invalid');return;}
      if(cameraOwner&&!share.viewers.has(peer.id)&&[...this.cameras.values()].filter(item=>item.viewers.has(peer.id)).length>=4){reject('busy');return;}
      if(share.viewers.has(peer.id)&&!message.restart)return;
      const link=id();share.viewers.set(peer.id,link);share.routes.delete(peer.id);
      this.media(client,share,{type:'screen:watching',owner:share.owner,session:share.session,link});
      this.media(this.clients.get(share.owner),share,{type:'screen:viewer',viewer:peer.id,session:share.session,link,joined:true});this.notifyAll();return;
    }
    if(message.type==='screen:unwatch'){
      if(share.viewers.delete(peer.id)){share.routes.delete(peer.id);share.featured.delete(peer.id);this.media(this.clients.get(share.owner),share,{type:'screen:viewer',session:share.session,viewer:peer.id,joined:false});this.notifyAll();}return;
    }
    if(message.type==='screen:focus'&&cameraOwner&&share.viewers.has(peer.id)){
      if(share.featured.has(peer.id)===!!message.featured)return;
      for(const camera of this.cameras.values())camera.featured.delete(peer.id);
      if(message.featured)share.featured.add(peer.id);
      this.notifyAll();return;
    }
    if(message.type==='screen:route'){
      const owner=peer.id===share.owner,viewer=owner?message.to:peer.id,target=this.clients.get(message.to);
      if(typeof message.relay!=='boolean'||!target||target.peer.room!==room||!share.viewers.has(viewer)||(!owner&&message.to!==share.owner)||share.viewers.get(viewer)!==message.link){reject('forbidden');return;}
      const hops=message.relay?([1,2].includes(message.relayHops)?message.relayHops:2):0;
      share.routes.set(viewer,hops);this.notifyAll();return;
    }
    if(message.type==='screen:refresh'){
      if(peer.id===share.owner||message.to!==share.owner||share.viewers.get(peer.id)!==message.link){reject('forbidden');return;}
      this.media(this.clients.get(share.owner),share,{type:'screen:refresh',session:share.session,link:message.link,from:peer.id});return;
    }
    if(message.type==='screen:stop'||message.type==='screen:update'){
      if(peer.id!==share.owner){reject('forbidden');return;}
      if(message.type==='screen:stop'){this.removeShare(share);this.notifyAll();return;}
      if(typeof message.paused!=='boolean'||!modes.has(message.mode)||(cameraOwner?message.surface!=='camera':!surfaces.has(message.surface))){reject('invalid');return;}
      Object.assign(share,{paused:message.paused,mode:message.mode,surface:message.surface});this.notify(room);return;
    }
    if(message.type==='screen:signal'){
      const owner=peer.id===share.owner,target=this.clients.get(message.to),viewer=owner?message.to:peer.id;
      if(!target||target.peer.room!==room||message.to===peer.id||(!owner&&message.to!==share.owner)||share.viewers.get(viewer)!==message.link){reject('forbidden');return;}
      const description=message.description,candidates=message.candidates;
      if(description){
        if(candidates||description.type!==(owner?'offer':'answer')||typeof description.sdp!=='string'||description.sdp.length>24000){reject('invalid');return;}
        // Screen connections are video-only; program sound continues over the existing music lane.
        if(!/^m=video /m.test(description.sdp)||/^m=(?:audio|application) /m.test(description.sdp)){reject('invalid');return;}
      }else if(!Array.isArray(candidates)||candidates.length<1||candidates.length>16||candidates.some(c=>!c||typeof c.candidate!=='string'||c.candidate.length>1500||(c.sdpMid!==null&&c.sdpMid!==undefined&&(typeof c.sdpMid!=='string'||c.sdpMid.length>32))||(c.sdpMLineIndex!==null&&c.sdpMLineIndex!==undefined&&(!Number.isInteger(c.sdpMLineIndex)||c.sdpMLineIndex<0||c.sdpMLineIndex>8))||(c.usernameFragment!==null&&c.usernameFragment!==undefined&&(typeof c.usernameFragment!=='string'||c.usernameFragment.length>256)))){reject('invalid');return;}
      this.media(target,share,{type:'screen:signal',from:peer.id,session:share.session,link:message.link,...(description?{description:{type:description.type,sdp:description.sdp}}:{candidates:candidates.map(c=>({candidate:c.candidate,sdpMid:c.sdpMid??null,sdpMLineIndex:c.sdpMLineIndex??null,usernameFragment:c.usernameFragment??null}))})});return;
    }
    reject('invalid');
  }
}
