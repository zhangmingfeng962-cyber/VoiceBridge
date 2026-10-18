class RoomMixer extends AudioWorkletProcessor{
  constructor(){super();this.streams=new Map();this.master=1;this.auto=true;this.ticks=0;this.decoded=0;this.port.onmessage=({data:m})=>{
    if(m.type==='master')this.master=m.value;
    if(m.type==='auto')this.auto=m.value;
    if(m.type==='remove'){for(const key of this.streams.keys())if(key.startsWith(m.id+':'))this.streams.delete(key);}
    if(m.type==='gain'){const s=this.streams.get(m.key);if(s)s.gain=m.value;}
    if(m.type==='packet'){
      let s=this.streams.get(m.key);if(!s){s={ring:new Float32Array(192000),read:0,write:0,phase:0,playing:false,target:100,jitter:0,last:0,adjust:0,under:0,trims:0,gain:m.gain??1,peak:0};this.streams.set(m.key,s);}
      const now=m.arrival;if(s.last)s.jitter+=(Math.abs(now-s.last-20)-s.jitter)/16;s.last=now;
      if(this.auto){const desired=Math.min(400,Math.max(60,60+s.jitter*4));if(desired>s.target)s.target=desired;else if(now-s.adjust>5000){s.target=Math.max(desired,s.target-10);s.adjust=now;}}
      const values=new Float32Array(m.samples);if(s.write-s.read+values.length/2>38400){s.read=Math.max(s.read,s.write-Math.round(s.target*48));s.phase=0;s.trims++;}
      for(let i=0;i<values.length;i+=2,s.write++){s.ring[s.write%96000*2]=values[i];s.ring[s.write%96000*2+1]=values[i+1];}s.peak=m.peak;this.decoded++;
    }
  };}
  process(_,outputs){const [left,right]=outputs[0];left.fill(0);right.fill(0);
    for(const s of this.streams.values()){
      if(!s.playing&&s.write-s.read>=s.target*48)s.playing=true;if(!s.playing)continue;
      const rate=s.write-s.read>(s.target+35)*48?1.002:s.write-s.read<(s.target-25)*48?.998:1;
      for(let i=0;i<left.length;i++){
        if(s.read+1>=s.write){s.playing=false;s.phase=0;s.under++;if(this.auto)s.target=Math.min(400,s.target+40);break;}
        const a=s.read%96000*2,b=(s.read+1)%96000*2;
        left[i]+=(s.ring[a]+(s.ring[b]-s.ring[a])*s.phase)*s.gain;right[i]+=(s.ring[a+1]+(s.ring[b+1]-s.ring[a+1])*s.phase)*s.gain;
        s.phase+=rate;while(s.phase>=1){s.phase--;s.read++;}
      }
    }
    for(let i=0;i<left.length;i++){left[i]=Math.max(-1,Math.min(1,left[i]*this.master));right[i]=Math.max(-1,Math.min(1,right[i]*this.master));}
    if(++this.ticks%80===0)this.port.postMessage({decoded:this.decoded,streams:[...this.streams].map(([id,s])=>({id,targetMs:Math.round(s.target),bufferedMs:Math.round((s.write-s.read)/48),underruns:s.under,trims:s.trims,peak:s.peak}))});return true;
  }
}
class RoomCapture extends AudioWorkletProcessor{
  constructor(options){super();this.channels=options.processorOptions.channels;this.buffer=new Float32Array(960*this.channels);this.used=0;this.enabled=false;this.gain=1;this.epoch=0;this.port.onmessage=({data:m})=>{this.enabled=m.enabled;this.gain=m.gain;this.epoch=m.epoch;this.used=0;};}
  process(inputs,outputs){for(const channel of outputs[0])channel.fill(0);const source=inputs[0];if(!this.enabled||!source?.length)return true;
    for(let n=0;n<source[0].length;n++){
      for(let c=0;c<this.channels;c++){let value=this.channels===1?source.reduce((sum,ch)=>sum+ch[n],0)/source.length:source[Math.min(c,source.length-1)][n];value*=this.gain;this.buffer[this.used*this.channels+c]=Math.max(-.98,Math.min(.98,Number.isFinite(value)?value:0));}
      if(++this.used===960){const pcm=this.buffer.buffer;this.port.postMessage({pcm,epoch:this.epoch},[pcm]);this.buffer=new Float32Array(960*this.channels);this.used=0;}
    }return true;
  }
}
registerProcessor('voicebridge-room-mixer',RoomMixer);registerProcessor('voicebridge-room-capture',RoomCapture);
