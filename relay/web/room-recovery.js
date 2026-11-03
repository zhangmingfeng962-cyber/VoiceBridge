const hex=(s,n)=>typeof s==='string'&&new RegExp('^[a-f0-9]{'+n+'}$').test(s);
const gain=(n,max,fallback)=>Number.isFinite(n)?Math.max(0,Math.min(max,n)):fallback;
function levels(value,max){const result={};for(const [id,n]of Object.entries(value&&typeof value==='object'?value:{})){if(hex(id,32)&&Object.keys(result).length<8)result[id]=gain(n,max,100);}return result;}
function controls(value={}){return {microphoneGain:gain(value.microphoneGain,400,100),speakerGain:gain(value.speakerGain,200,100),musicGain:gain(value.musicGain,100,100),talkMode:value.talkMode==='ptt'?'ptt':'free',talkKey:Number.isInteger(value.talkKey)&&value.talkKey>=8&&value.talkKey<=254?value.talkKey:86,microphoneId:typeof value.microphoneId==='string'?value.microphoneId.slice(0,256):'',outputId:typeof value.outputId==='string'?value.outputId.slice(0,256):'',autoBuffer:value.autoBuffer!==false,memberVoice:levels(value.memberVoice,200),memberMusic:levels(value.memberMusic,100)};}
export function saveRecovery(storage,key,accountId,value,options,admission,now=Date.now()){
 try{if(!hex(accountId,32)||!hex(value?.memberId,32)||!hex(value?.token,64))return false;storage.setItem(key,JSON.stringify({accountId,value,at:now,microphone:options.microphone===true,options:controls(options),admission:hex(admission?.token,64)&&hex(admission?.key,64)?{token:admission.token,key:admission.key}:null}));return true;}catch{return false;}
}
export function readRecovery(storage,key,accountId,now=Date.now()){
 try{const raw=storage.getItem(key);if(!raw||raw.length>16384)return null;const saved=JSON.parse(raw);if(!hex(accountId,32)||saved.accountId!==accountId||!Number.isFinite(saved.at)||now-saved.at>=90000||saved.at>now+5000||!hex(saved.value?.memberId,32)||!hex(saved.value?.token,64))return null;return {value:saved.value,microphone:saved.microphone===true,options:controls(saved.options||{}),admission:hex(saved.admission?.token,64)&&hex(saved.admission?.key,64)?saved.admission:null};}catch{return null;}
}
