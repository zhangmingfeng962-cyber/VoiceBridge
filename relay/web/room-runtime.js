export class RoomRuntime{
 constructor(){this.worker=new Worker('/room-runtime.worker.js',{type:'module',name:'VoiceBridge audio'});this.socket=null;this.worker.onmessage=({data})=>{const socket=this.socket;if(!socket)return;
  if(data.type==='needAttachment')window.chrome?.webview?.postMessage({type:'refreshAttachment'});
  if(data.type==='open'){socket.readyState=1;socket.onopen?.();}
  if(data.type==='message')socket.onmessage?.({data:data.data});
  if(data.type==='close'){socket.readyState=3;socket.onclose?.({code:data.code});}
  if(data.type==='error')socket.onerror?.();
  if(data.type==='reconnecting'){socket.onreconnecting?.(data);}
  if(data.type==='resume')socket.onresume?.(data.value);
  if(data.type==='notice')this.onnotice?.(data.message);if(data.type==='codecError')this.oncodecerror?.(data.kind,data.message);
 };if(window.chrome?.webview)window.chrome.webview.addEventListener('message',event=>{if(event.data?.type==='attachment'&&/^[a-f0-9]{64}$/.test(event.data.ticket))this.post({type:'attachment',ticket:event.data.ticket});});}
 post(value,transfer=[]){this.worker.postMessage(value,transfer);}
 configure(value){this.post({type:'desired',value});}
 attachMixer(port){this.post({type:'mixer',port},[port]);}
 attachCapture(kind,port){this.post({type:'capture',kind,port},[port]);}
 stopCapture(kind){this.post({type:'stopCapture',kind});}
 setGain(id,kind,value){this.post({type:'gain',id,kind,value});}
 remove(id){this.post({type:'remove',id});}
 close(){this.post({type:'close'});this.worker.terminate();}
}
export class RuntimeSocket{
 constructor(url,runtime,roomId,key,role,requireAttachment=false,join=null){this.runtime=runtime;this.readyState=0;this.bufferedAmount=0;runtime.socket=this;runtime.post({type:'connect',url,roomId,key,role,requireAttachment,join});}
 send(data){if(this.readyState!==1)return;this.runtime.post({type:'send',data});}
 close(code=1000,reason=''){this.readyState=3;this.runtime.post({type:'close',code,reason});}
}
