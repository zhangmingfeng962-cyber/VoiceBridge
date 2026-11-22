const reduced=matchMedia('(prefers-reduced-motion: reduce)');
let cleanup=[];
let revision=0;

async function updateEffects(){
  const current=++revision;
  for(const stop of cleanup)stop?.();
  cleanup=[];
  if(reduced.matches||document.hidden)return;
  const [dots,title]=await Promise.all([import('./dot-field.js'),import('./tech-text.js')]);
  if(current!==revision||document.hidden)return;
  cleanup=[
    dots.mountDotField(document.getElementById('homeDots'),{
      dotRadius:1.25,dotSpacing:13,gradientFrom:'#2c5a67',gradientTo:'#80c9d5',gradientAngle:72,
      glowColor:'#74d6e2',glowIntensity:.45,glowRadius:150,bulgeStrength:.24,
      baseOpacity:.58,maxDevicePixelRatio:1.5,holdToZoom:false,intro:false
    }),
    title.mountTechText(document.getElementById('homeTitle'),{
      text:'VoiceBridge',fontFamily:'Segoe UI,sans-serif',fontWeight:750,fontSize:150,
      color:'#f5f5f2',accentColor:'#80c9d5',align:'left',selection:false,labels:false,
      draggable:false,specks:5,reach:115,bounce:.18,sweep:false,intro:'none'
    })
  ];
}
const refresh=()=>void updateEffects().catch(()=>{});
reduced.addEventListener('change',refresh);
document.addEventListener('visibilitychange',refresh);
refresh();
