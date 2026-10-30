import {ScreenShare} from './screen.js';
const el=id=>document.getElementById(id),clients=[],checks=[];
const wait=async(predicate,label,timeout=18000)=>{const start=performance.now();while(performance.now()-start<timeout){if(await predicate())return;await new Promise(resolve=>setTimeout(resolve,60));}throw Error('Timeout: '+label);};
const record=label=>{checks.push(label);const li=document.createElement('li');li.textContent='PASS · '+label;el('results').append(li);};
function sample(video){if(video.readyState<2)return [0,0,0];const canvas=document.createElement('canvas');canvas.width=canvas.height=2;canvas.getContext('2d').drawImage(video,0,0,2,2);return [...canvas.getContext('2d').getImageData(0,0,1,1).data];}
async function frames(client){for(const item of client.screen.links.values()){const stats=await item.pc.getStats();for(const stat of stats.values())if(stat.type==='inbound-rtp'&&stat.kind==='video')return stat.framesDecoded||0;}return 0;}
async function member(config,index){
  const socket=new WebSocket(`${location.origin.replace('http:','ws:')}/api/rooms/${config.id}/voice`),client={socket,id:null,screen:null,state:{owner:null},errors:[],counts:{sent:0,received:0,applied:0,mdns:0},video:el('video'+index)};clients.push(client);
  const send=message=>{if(message.candidates)client.counts.sent+=message.candidates.length;if(socket.readyState===1)socket.send(JSON.stringify(message));};
  socket.onopen=()=>send({type:'join',version:3,role:'browser',token:config.listenToken,name:['画面发起者','观看者一','观看者二'][index]});
  socket.onmessage=event=>{if(typeof event.data!=='string')return;const message=JSON.parse(event.data);if(message.candidates)client.counts.received+=message.candidates.length;
    if(message.type==='welcome'){client.id=message.id;client.screen=new ScreenShare({selfId:client.id,send,createPeer:configuration=>{const pc=new RTCPeerConnection(configuration),add=pc.addIceCandidate.bind(pc);pc.addIceCandidate=async candidate=>{client.counts.applied++;if(candidate.candidate.includes('.local'))client.counts.mdns++;return add(candidate);};return pc;},capture:async()=>window.fixtureVideo(index===0?'Synthetic A':'Synthetic B',index===0?'#15665c':'#994526'),onState:state=>{client.state=state;el('state'+index).textContent=!state.owner?'无人共享':state.paused?'共享已暂停':state.self?'正在共享':'正在观看';client.video.hidden=!!state.paused;},onVideo:stream=>{client.video.srcObject=stream;if(stream)void client.video.play().catch(()=>{});},onStatus:message=>client.errors.push(message)});}
    if(message.type.startsWith('screen:'))void client.screen?.receive(message);
  };
  socket.onclose=()=>{client.screen?.close();client.state={owner:null};el('state'+index).textContent='已断开';};await wait(()=>client.screen?.supported,'screen capability');return client;
}
el('run').onclick=async()=>{
  el('run').disabled=true;el('status').textContent='测试中：真实 WebSocket + WebRTC，视频来自合成画布';
  try{
    const config=await fetch('/__fixture/config').then(r=>r.json()),a=await member(config,0),b=await member(config,1),c=await member(config,2);
    await a.screen.select();await wait(async()=>await frames(b)>4&&await frames(c)>4,'two real WebRTC viewers');
    record('同一共享画面传给两位真实 WebRTC 观看者');
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
