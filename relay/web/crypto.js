export const hex=bytes=>[...bytes].map(v=>v.toString(16).padStart(2,'0')).join('');
export const fromHex=value=>Uint8Array.from(value.match(/../g),part=>parseInt(part,16));
const encode=new TextEncoder();
const b64=bytes=>{let value='';for(const n of bytes)value+=String.fromCharCode(n);return btoa(value);};
const un64=value=>Uint8Array.from(atob(value),c=>c.charCodeAt(0));
export class RoomCrypto{
  constructor(room,root){this.room=room;this.root=root;this.keys=new Map();}
  key(purpose){if(!this.keys.has(purpose))this.keys.set(purpose,(async()=>{const base=await crypto.subtle.importKey('raw',fromHex(this.root),'HKDF',false,['deriveKey']);return crypto.subtle.deriveKey({name:'HKDF',hash:'SHA-256',salt:encode.encode(this.room),info:encode.encode(purpose)},base,{name:'AES-GCM',length:256},false,['encrypt','decrypt']);})());return this.keys.get(purpose);}
  async sealAudio(kind,id,sequence,opus){
    const iv=crypto.getRandomValues(new Uint8Array(12)),encrypted=await crypto.subtle.encrypt({name:'AES-GCM',iv,additionalData:encode.encode(`${this.room}|${kind}|${id}|${sequence}`)},await this.key(`VoiceBridge audio v2|${kind}|${id}`),opus);
    const data=new Uint8Array(21+encrypted.byteLength),view=new DataView(data.buffer);view.setUint32(0,0x32414256,true);data[4]=kind;view.setUint32(5,sequence,true);data.set(iv,9);data.set(new Uint8Array(encrypted),21);return data;
  }
  async openAudio(data){
    if(data.byteLength<54||data.byteLength>1328)throw new Error('Audio frame invalid');const id=hex(new Uint8Array(data,0,16)),view=new DataView(data,16),kind=view.getUint8(4),sequence=view.getUint32(5,true);
    if(view.getUint32(0,true)!==0x32414256||kind>1)throw new Error('Audio frame invalid');
    const opus=await crypto.subtle.decrypt({name:'AES-GCM',iv:new Uint8Array(data,25,12),additionalData:encode.encode(`${this.room}|${kind}|${id}|${sequence}`)},await this.key(`VoiceBridge audio v2|${kind}|${id}`),data.slice(37));return {id,kind,sequence,opus};
  }
  async sealControl(id,connectionId,sequence,command){const iv=crypto.getRandomValues(new Uint8Array(12)),data=await crypto.subtle.encrypt({name:'AES-GCM',iv,additionalData:encode.encode(`${this.room}|control|${id}|${connectionId}|${sequence}`)},await this.key(`VoiceBridge control v2|${id}|${connectionId}`),encode.encode(JSON.stringify(command)));return {type:'control',connectionId,sequence,iv:b64(iv),data:b64(new Uint8Array(data))};}
  async openState(id,connectionId,message){if(message.connectionId!==connectionId)return null;const plain=await crypto.subtle.decrypt({name:'AES-GCM',iv:un64(message.iv),additionalData:encode.encode(`${this.room}|state|${id}|${connectionId}|${message.sequence}`)},await this.key(`VoiceBridge state v2|${id}|${connectionId}`),un64(message.data));return JSON.parse(new TextDecoder().decode(plain));}
}
