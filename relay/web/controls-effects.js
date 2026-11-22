// SquishSwitch and WakeSlider interactions adapted to native controls.
const tracks=new WeakMap();
const switches='button:is(#microphone,#speakerToggle,#share,#shareTab,#noiseSuppression,#screenSound,#cameraStart,.memberListen)[aria-pressed]';
const reduced=matchMedia('(prefers-reduced-motion: reduce)');

function syncSwitch(button){
  if(!button.matches(switches))return;
  const pressed=button.getAttribute('aria-pressed');
  if(pressed===null)return;
  button.classList.add('squishSwitch');
  button.setAttribute('role','switch');
  button.setAttribute('aria-checked',pressed);
}

function renderWave(track,animated=false){
  const {input,bars}=track;
  const from=input.min===''?0:Number(input.min),to=input.max===''?100:Number(input.max);
  const percent=Math.max(0,Math.min(1,(Number(input.value)-from)/(to-from)||0));
  if(percent===track.percent&&!animated)return;
  const changed=percent-(track.percent??percent);
  track.percent=percent;
  track.handle.style.left=(percent*100)+'%';
  const index=percent*(bars.length-1);
  for(let i=0;i<bars.length;i++){
    const bar=bars[i],active=i<=index;
    bar.classList.toggle('filled',active);
    if(!animated||reduced.matches||!changed)continue;
    const distance=index-i;
    const reach=distance*changed>=0?6:2.8;
    const intensity=Math.max(0,1-Math.abs(distance)/reach);
    if(intensity===0)continue;
    bar.getAnimations().forEach(animation=>animation.cancel());
    bar.animate([{transform:'scaleY('+String(1+intensity*2.2)+')'},{transform:'scaleY(1)'}],{
      duration:220,easing:'cubic-bezier(.22,1,.36,1)'
    });
  }
}

function mountWave(input){
  if(tracks.has(input))return;
  const root=document.createElement('span'),visual=document.createElement('span'),handle=document.createElement('span');
  const bars=Array.from({length:24},()=>document.createElement('i'));
  root.className='wakeSlider';visual.className='wakeBars';handle.className='wakeHandle';
  visual.setAttribute('aria-hidden','true');handle.setAttribute('aria-hidden','true');
  visual.append(...bars);
  input.before(root);root.append(visual,handle,input);
  const track={input,bars,handle};
  tracks.set(input,track);
  renderWave(track);
  input.addEventListener('input',()=>renderWave(track,true));
}

export function enhanceControls(root=document){
  if(root.matches?.(switches))syncSwitch(root);
  if(root.matches?.('input[type=range]'))mountWave(root);
  root.querySelectorAll?.(switches).forEach(syncSwitch);
  root.querySelectorAll?.('input[type=range]').forEach(mountWave);
}

export function refreshVolumeEffects(root=document){
  root.querySelectorAll('input[type=range]').forEach(input=>{
    const track=tracks.get(input);
    if(track)renderWave(track);
  });
}

export function installControlEffects(){
  enhanceControls();
  new MutationObserver(records=>{
    for(const record of records){
      if(record.type==='attributes')syncSwitch(record.target);
      else record.addedNodes.forEach(node=>enhanceControls(node));
    }
  }).observe(document.body,{childList:true,subtree:true,attributes:true,attributeFilter:['aria-pressed']});
}
