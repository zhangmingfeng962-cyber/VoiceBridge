import {WebSocket} from 'ws';

// Keep application queues bounded. Never build seconds of obsolete audio in TCP.
export class Outbound {
  constructor(socket,now=Date.now){this.socket=socket;this.now=now;this.controls=[];this.voice=[];this.music=[];this.dropped=0;}
  push(value,binary=false){
    if(this.socket.readyState!==WebSocket.OPEN)return;
    const entry={data:binary?value:JSON.stringify(value),binary,time:this.now()};
    if(binary){
      const queue=value[20]===0?this.voice:this.music;
      if(queue.length>=6){queue.shift();this.dropped++;}queue.push(entry);
    }else{
      const key=['members','state','screen:state'].includes(value.type)?value.type+(value.connectionId||''):null;
      entry.key=key;
      const previous=key?this.controls.findIndex(x=>x.key===key):-1;
      if(previous>=0)this.controls.splice(previous,1);
      if(this.controls.length>=64){this.socket.close(1013,'Control queue stalled');return;}
      this.controls.push(entry);
    }
    this.flush();
  }
  flush(){
    const socket=this.socket;if(socket.readyState!==WebSocket.OPEN)return;
    const now=this.now();
    for(const queue of [this.voice,this.music])while(queue.length&&now-queue[0].time>120){queue.shift();this.dropped++;}
    if(socket.bufferedAmount>1048576||this.controls[0]&&now-this.controls[0].time>5000){socket.close(1013,'Network stalled; reconnect');return;}
    let budget=12;
    while(budget-->0&&socket.bufferedAmount<16384){
      const entry=this.controls.shift()||this.voice.shift()||this.music.shift();if(!entry)break;
      socket.send(entry.data,{binary:entry.binary},error=>{if(error&&socket.readyState===WebSocket.OPEN)socket.close(1013,'Network interrupted');});
    }
  }
}
