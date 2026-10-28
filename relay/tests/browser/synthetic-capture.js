// Test-only capture provider. This file is never packaged in the production relay.
(()=>{
  let screens=0,mics=0,cancelNext=false;
  function synthetic(name='合成软件窗口',color='#15665c'){
    const canvas=document.createElement('canvas');canvas.width=1280;canvas.height=720;
    const context=canvas.getContext('2d');let tick=0;
    const draw=()=>{context.fillStyle=color;context.fillRect(0,0,1280,720);context.fillStyle='#e8fff7';context.font='bold 44px system-ui';context.fillText(name,65,110);context.font='25px system-ui';context.fillText('VoiceBridge · synthetic video · no real screen capture',65,165);context.fillText('FRAME '+tick++,65,220);context.fillStyle='#6ceac5';context.fillRect(65+(tick*4)%1050,530,70,70);};
    draw();const timer=setInterval(draw,33),stream=canvas.captureStream(30),track=stream.getVideoTracks()[0],stop=track.stop.bind(track),settings=track.getSettings.bind(track);
    track.getSettings=()=>({...settings(),displaySurface:'window'});track.stop=()=>{clearInterval(timer);stop();};return stream;
  }
  navigator.mediaDevices.getDisplayMedia=async options=>{screens++;if(cancelNext){cancelNext=false;throw Object.assign(Error('Synthetic cancellation'),{name:'NotAllowedError'});}if(options.audio!==false&&options.audio!==undefined)throw Error('Fixture only supports video capture');return synthetic(screens%2?'合成软件窗口 A':'合成桌面 B',screens%2?'#15665c':'#264ca6');};
  navigator.mediaDevices.getUserMedia=async()=>{mics++;const context=new AudioContext({sampleRate:48000}),osc=context.createOscillator(),gain=context.createGain(),out=context.createMediaStreamDestination();gain.gain.value=0;osc.connect(gain).connect(out);osc.start();const track=out.stream.getAudioTracks()[0],stop=track.stop.bind(track);track.stop=()=>{osc.stop();stop();void context.close();};return out.stream;};
  navigator.mediaDevices.enumerateDevices=async()=>[{kind:'audioinput',deviceId:'synthetic',label:'合成输入 · 不读取真实麦克风'}];
  window.fixtureVideo=synthetic;
  document.addEventListener('DOMContentLoaded',()=>{const badge=document.createElement('div');badge.id='fixtureCaptureNotice';badge.style='position:fixed;left:12px;bottom:10px;background:#f9cf7a;color:#101927;padding:9px 14px;border-radius:8px;z-index:10000;font:13px system-ui';document.body.append(badge);const cancel=document.createElement('button');cancel.textContent='模拟取消一次选择';cancel.style='position:fixed;right:12px;bottom:10px;z-index:10000';cancel.onclick=()=>{cancelNext=true;};document.body.append(cancel);setInterval(()=>badge.textContent=`合成验证：没有采集真实屏幕或麦克风 · 画面请求 ${screens} · 合成输入请求 ${mics}`,500);});
})();
