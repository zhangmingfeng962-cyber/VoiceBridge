/* Human voice only. Independent from the 48 kHz stereo music processor. */
class VoiceBridgeVoice extends AudioWorkletProcessor {
  constructor(){super();this.peers=new Map();this.capture=false;this.frame=new Int16Array(320);this.used=0;this.phase=0;this.low1=this.low2=0;this.port.onmessage=({data:m})=>{
    if(m.type==='capture'){this.capture=m.enabled;this.captureId=m.id;this.used=this.phase=0;this.low1=this.low2=0;}
    if(m.type==='remove')this.peers.delete(m.id);
    if(m.type==='voice'){
      let p=this.peers.get(m.id);if(!p){p={ring:new Float32Array(16000),read:0,write:0,playing:false,phase:0,last:-1};this.peers.set(m.id,p);}
      if(p.last>=0&&m.sequence!==p.last+1){p.read=p.write;p.playing=false;}p.last=m.sequence;
      const samples=new Float32Array(m.samples);
      if(p.write-p.read+samples.length>12800){p.read=Math.max(p.read,p.write-2560);p.playing=false;}
      for(const value of samples)p.ring[p.write++%16000]=value;
    }
  };}
  process(inputs,outputs){
    const output=outputs[0][0],input=inputs[0]?.[0];output.fill(0);
    for(const p of this.peers.values()){
      if(!p.playing&&p.write-p.read>=2560)p.playing=true;
      if(!p.playing)continue;
      for(let n=0;n<output.length;n++){
        if(p.read+1>=p.write){p.playing=false;break;}
        const a=p.ring[p.read%16000],b=p.ring[(p.read+1)%16000];output[n]+=(a+(b-a)*p.phase)*.65;
        p.phase+=16000/sampleRate;while(p.phase>=1){p.read++;p.phase--;}
      }
    }
    for(let n=0;n<output.length;n++)output[n]=Math.max(-1,Math.min(1,output[n]));
    if(this.capture&&input){
      const alpha=1-Math.exp(-2*Math.PI*6500/sampleRate);
      for(const sample of input){
        // Two low-pass stages before speech-rate downsampling.
        this.low1+=alpha*(sample-this.low1);this.low2+=alpha*(this.low1-this.low2);this.phase+=16000;
        if(this.phase>=sampleRate){this.phase-=sampleRate;const value=Math.max(-1,Math.min(1,this.low2));this.frame[this.used++]=Math.round(value*(value<0?32768:32767));
          if(this.used===320){const pcm=this.frame.buffer;this.port.postMessage({type:'microphone',pcm,id:this.captureId},[pcm]);this.frame=new Int16Array(320);this.used=0;}
        }
      }
    }
    return true;
  }
}
registerProcessor('voicebridge-voice',VoiceBridgeVoice);
