import {ScreenShare} from './screen.js';
import {BrowserAudio} from './audio.js';
import {RoomCrypto} from './crypto.js';
const el=id=>document.getElementById(id),clients=[],checks=[];
const wait=async(predicate,label,timeout=18000)=>{const start=performance.now();while(performance.now()-start<timeout){if(await predicate())return;await new Promise(resolve=>setTimeout(resolve,60));}throw Error('Timeout: '+label);};
const record=label=>{checks.push(label);const li=document.createElement('li');li.textContent='PASS · '+label;el('results').append(li);};
function sample(video){if(video.readyState<2)return [0,0,0];const canvas=document.createElement('canvas');canvas.width=canvas.height=2;canvas.getContext('2d').drawImage(video,0,0,2,2);return [...canvas.getContext('2d').getImageData(0,0,1,1).data];}
async function frames(client){for(const item of client.screen.links.values()){const stats=await item.pc.getStats();for(const stat of stats.values())if(stat.type==='inbound-rtp'&&stat.kind==='video')return stat.framesDecoded||0;}return 0;}
async function member(config,index,withAudio=false){
  const socket=new WebSocket(`${location.origin.replace('http:','ws:')}/api/rooms/${config.id}/voice`),client={socket,id:null,screen:null,state:{owner:null},errors:[],counts:{sent:0,received:0,applied:0,mdns:0},video:el('video'+index)};clients.push(client);
  socket.binaryType='arraybuffer';client.renewals=0;
  const send=message=>{if(message.candidates)client.counts.sent+=message.candidates.length;if(socket.readyState===1)socket.send(JSON.stringify(message));};
  socket.onopen=()=>send({type:'join',version:3,role:'browser',token:config.listenToken,name:['画面发起者','观看者一','观看者二'][index]});
  socket.onmessage=event=>{if(typeof event.data!=='string'){if(client.audio&&client.crypto)client.receiving=(client.receiving||Promise.resolve()).then(async()=>client.audio?.receive(await client.crypto.openAudio(event.data))).catch(error=>client.errors.push(error.message));return;}const message=JSON.parse(event.data);if(message.candidates)client.counts.received+=message.candidates.length;
    if(message.type==='welcome'){client.id=message.id;client.screen=new ScreenShare({selfId:client.id,send,createPeer:configuration=>{const pc=new RTCPeerConnection(configuration),add=pc.addIceCandidate.bind(pc);pc.addIceCandidate=async candidate=>{client.counts.applied++;if(candidate.candidate.includes('.local'))client.counts.mdns++;return add(candidate);};return pc;},capture:async()=>window.fixtureVideo(index===0?'Synthetic A':'Synthetic B',index===0?'#15665c':'#994526'),onState:state=>{client.state=state;el('state'+index).textContent=!state.owner?'无人共享':state.paused?'共享已暂停':state.self?'正在共享':'正在观看';client.video.hidden=!!state.paused;},onVideo:stream=>{client.video.srcObject=stream;if(stream)void client.video.play().catch(()=>{});},onStatus:message=>client.errors.push(message)});}
    if(message.type==='screen:hello'){if(client.lastExpiry&&message.expires>client.lastExpiry)client.renewals++;client.lastExpiry=message.expires;}if(message.type.startsWith('screen:'))void client.screen?.receive(message);
  };
  socket.onclose=()=>{client.screen?.close();void client.audio?.stop();client.state={owner:null};el('state'+index).textContent='已断开';};await wait(()=>client.screen?.supported,'screen capability');
  if(withAudio){
    client.crypto=new RoomCrypto(config.id,config.key);let sequences=[0,0],pending=0,sending=Promise.resolve();
    client.audio=new BrowserAudio((kind,opus)=>{if(pending>=8||socket.bufferedAmount>16384)return;pending++;const created=performance.now();sending=sending.then(async()=>{if(performance.now()-created>120)return;const packet=await client.crypto.sealAudio(kind,client.id,sequences[kind]++,opus);if(socket.readyState===1)socket.send(packet);}).finally(()=>pending--);},stats=>client.audioStats=stats,error=>client.errors.push(error));
    await client.audio.start();client.audio.configure({microphone:true,noiseSuppression:false,speakerGain:0});await client.audio.microphone(true);
    if(index===0)await client.audio.addCapture(1,window.fixtureSound(),++client.audio.shareEpoch);
    send({type:'flags',microphone:true,music:index===0,source:index===0?'合成立体声测试音':''});
  }
  return client;
}
el('run').onclick=async()=>{
  el('run').disabled=true;el('status').textContent='测试中：真实 WebSocket + WebRTC，视频来自合成画布';
  try{
    const config=await fetch('/__fixture/config').then(r=>r.json()),a=await member(config,0),b=await member(config,1),c=await member(config,2);
    await a.screen.select();await wait(async()=>await frames(b)>4&&await frames(c)>4,'two real WebRTC viewers');
    record('同一共享画面传给两位真实 WebRTC 观看者');
    if(config.turnTransport){for(const client of [b,c])for(const link of client.screen.links.values()){const stats=await link.pc.getStats(),values=[...stats.values()],transport=values.find(x=>x.type==='transport'&&x.selectedCandidatePairId),pair=stats.get(transport?.selectedCandidatePairId)||values.find(x=>x.type==='candidate-pair'&&x.nominated&&x.state==='succeeded');if(!pair||stats.get(pair.localCandidateId)?.candidateType!=='relay'||stats.get(pair.remoteCandidateId)?.candidateType!=='relay')throw Error('Expected selected relay candidates at both endpoints');}record('两位观看者均使用实际选中的杭州中继候选');}
    const first=sample(b.video);if(!(first[1]>first[2]))throw Error('Unexpected first source pixels');record('观看端收到来源 A 的真实视频像素');
    await a.screen.pause(true);await wait(()=>b.state.paused&&c.state.paused,'pause state');
    if([...a.screen.links.values()].some(item=>item.sender.track!==null))throw Error('Live video sender remained while paused');
    if(!b.video.hidden||el('state1').textContent!=='共享已暂停')throw Error('Pause placeholder missing');record('暂停时停止发送视频轨道并显示占位');
    const before=await frames(b);await a.screen.pause(false);await wait(async()=>!b.state.paused&&await frames(b)>before+4,'resume');record('恢复后继续发送同一来源');
    a.screen.capture=async()=>window.fixtureVideo('Synthetic replacement B','#264ca6');await a.screen.select(true);
    await wait(()=>{const color=sample(b.video);return color[2]>color[1]*1.5;},'replacement pixels');record('来源切换后观看端收到来源 B 像素');
    await a.screen.quality('clear',true);for(const item of a.screen.links.values()){await item.work;if(item.sender.getParameters().encodings[0].maxBitrate>4500000)throw Error('Bitrate limit missing');}record('浏览器实际发送器应用画质与码率限制');
    const old=a.screen.stream.getVideoTracks()[0];a.screen.stop();await wait(()=>!b.state.owner&&!c.state.owner,'stop');if(old.readyState!=='ended')throw Error('Capture track not released');record('结束后释放画面轨道和单人共享占用');
    await b.screen.select();await wait(async()=>c.state.owner===b.id&&await frames(c)>4,'next presenter');record('另一位成员可以接着共享');
    b.socket.close();await wait(()=>!c.state.owner,'presenter disconnect');record('共享者断线自动释放占用');
    const report={passed:true,checks,browser:navigator.userAgent,syntheticOnly:true,realScreenCaptured:false,realMicrophoneCaptured:false,turnTested:false};
    await fetch('/__fixture/report',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(report)});
    el('status').textContent='全部通过 · '+checks.length+' 项真实浏览器视频验证';el('status').dataset.passed='true';
  }catch(error){
    const diagnostics=[];for(const client of clients){const links=[];for(const item of client.screen?.links.values()||[]){const stats=await item.pc.getStats();links.push({owner:item.owner,connection:item.pc.connectionState,ice:item.pc.iceConnectionState,gathering:item.pc.iceGatheringState,signaling:item.pc.signalingState,local:item.pc.localDescription?.type,remote:item.pc.remoteDescription?.type,directions:item.pc.getTransceivers().map(t=>({requested:t.direction,current:t.currentDirection,stopped:t.stopped,track:t.sender.track?.readyState})),sdp:item.pc.localDescription?.sdp.split('\r\n').filter(l=>/^m=|^a=(?:send|recv|inactive|group|mid)/.test(l)),candidateTypes:[...stats.values()].filter(x=>x.type==='local-candidate'||x.type==='remote-candidate').map(x=>({type:x.type,protocol:x.protocol,candidateType:x.candidateType})),stats:[...stats.values()].filter(x=>['inbound-rtp','outbound-rtp'].includes(x.type)).map(x=>({type:x.type,kind:x.kind,framesDecoded:x.framesDecoded,framesSent:x.framesSent,bytesSent:x.bytesSent,bytesReceived:x.bytesReceived}))});}diagnostics.push({id:client.id,errors:client.errors,counts:client.counts,links});}
    el('status').textContent='FAIL · '+error.message;const pre=document.createElement('pre');pre.textContent=JSON.stringify(diagnostics,null,2);el('results').after(pre);await fetch('/__fixture/report',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({passed:false,error:error.message,checks,diagnostics})});
  }
};
window.addEventListener('pagehide',()=>clients.forEach(client=>{client.screen?.close();client.socket.close();}));

const soakButton=document.createElement('button');soakButton.id='soak';soakButton.textContent='开始20分钟稳定性验证';el('run').after(soakButton);
soakButton.onclick=async()=>{
  soakButton.disabled=true;el('run').disabled=true;const samples=[];
  try{
    for(const client of clients){client.screen?.close();void client.audio?.stop();client.socket.close();}clients.length=0;
    const config=await fetch('/__fixture/config').then(r=>r.json()),a=await member(config,0,true),b=await member(config,1,true),c=await member(config,2,true);
    await a.screen.select();await wait(async()=>await frames(b)>4&&await frames(c)>4&&clients.every(x=>x.audioStats?.decoded>5),'initial video and decoded stereo/voice',30000);
    const start=Date.now(),last=new Map();el('status').textContent='20分钟验证中 · 合成视频、人声与立体声音乐 · 不使用真实采集';
    while(Date.now()-start<1200000){
      const viewers=[];
      for(const client of [b,c]){
        const decoded=await frames(client),previous=last.get(client.id);if(previous&&decoded<=previous.frames&&Date.now()-previous.at>15000)throw Error('Video stopped progressing');if(!previous||decoded>previous.frames)last.set(client.id,{frames:decoded,at:Date.now()});
        const link=[...client.screen.links.values()][0];if(!link)throw Error('Video connection was lost');const stats=await link.pc.getStats(),values=[...stats.values()],transport=values.find(x=>x.type==='transport'&&x.selectedCandidatePairId),pair=stats.get(transport?.selectedCandidatePairId)||values.find(x=>x.type==='candidate-pair'&&x.state==='succeeded'&&x.nominated),video=values.find(x=>x.type==='inbound-rtp'&&x.kind==='video');
        const relay=pair&&(stats.get(pair.localCandidateId)?.candidateType==='relay'||stats.get(pair.remoteCandidateId)?.candidateType==='relay');
        if(config.turnTransport&&pair&&!relay)throw Error('Expected forced TURN path');
        viewers.push({frames:decoded,width:video?.frameWidth,height:video?.frameHeight,fps:video?.framesPerSecond,relay:!!relay,audioDecoded:client.audioStats?.decoded||0,renewals:client.renewals});
      }
      samples.push({seconds:Math.round((Date.now()-start)/1000),viewers});
      el('status').textContent='稳定性验证 '+Math.floor((Date.now()-start)/60000)+' / 20 分钟 · 两人画面与音频持续解码 · 凭据续期 '+Math.min(...clients.map(x=>x.renewals))+' 次';
      await fetch('/__fixture/report',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({running:true,syntheticOnly:true,realScreenCaptured:false,realMicrophoneCaptured:false,turnTransport:config.turnTransport,seconds:Math.round((Date.now()-start)/1000),samples})});
      await new Promise(resolve=>setTimeout(resolve,5000));
    }
    if(clients.some(x=>x.renewals<3))throw Error('Not enough credential renewal cycles');
    const report={passed:true,soak:true,syntheticOnly:true,realScreenCaptured:false,realMicrophoneCaptured:false,seconds:Math.round((Date.now()-start)/1000),turnTransport:config.turnTransport,renewals:clients.map(x=>x.renewals),samples};
    await fetch('/__fixture/report',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(report)});el('status').textContent='20分钟稳定性验证通过 · 视频、人声与立体声音乐持续解码';el('status').dataset.passed='true';
  }catch(error){el('status').textContent='FAIL · '+error.message;await fetch('/__fixture/report',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({passed:false,soak:true,error:error.message,samples})});}
  finally{for(const client of clients){client.screen?.close();await client.audio?.stop();client.socket.close();}}
};
