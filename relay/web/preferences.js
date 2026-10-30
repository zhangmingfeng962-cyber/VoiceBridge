const key='voicebridge.preferences.v1';
export const cleanNickname=value=>typeof value==='string'?value.replace(/[\p{C}]/gu,'').trim().slice(0,20):'';
export function readPreferences(storage){
  try{const raw=(storage??globalThis.localStorage).getItem(key);if(!raw||raw.length>4096)return {nickname:'朋友',noiseSuppression:true};const value=JSON.parse(raw);return {nickname:cleanNickname(value.nickname)||'朋友',noiseSuppression:typeof value.noiseSuppression==='boolean'?value.noiseSuppression:true};}
  catch{return {nickname:'朋友',noiseSuppression:true};}
}
export function savePreferences(value,storage){
  try{const previous=readPreferences(storage),next={version:1,nickname:cleanNickname(value.nickname??previous.nickname)||'朋友',noiseSuppression:typeof value.noiseSuppression==='boolean'?value.noiseSuppression:previous.noiseSuppression};(storage??globalThis.localStorage).setItem(key,JSON.stringify(next));return true;}catch{return false;}
}
