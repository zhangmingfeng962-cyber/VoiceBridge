import {getRooms,renderRooms,publicLink} from './lobby.js';
import {readPreferences,savePreferences,cleanNickname} from './preferences.js';
import {openRoom} from './open-room.js';
export function invitationTarget(value,origin){
  const input=String(value).trim();let url;try{url=new URL(input);}catch{throw Error('请粘贴完整房间邀请');}
  if(url.origin!==origin||url.username||url.password||url.search)throw Error('请使用本网站的完整房间邀请');
  if(/^\/rooms\/[a-f0-9]{32}$/.test(url.pathname)&&!url.hash)return url.pathname;
  if(!/^\/room\/[a-f0-9]{32}$/.test(url.pathname)||!/^#[a-f0-9]{64}\.[a-f0-9]{64}$/.test(url.hash))throw Error('邀请格式不正确，请朋友使用“邀请朋友”重新复制。');
  return url.pathname+url.hash;
}
if(typeof document!=='undefined'){
 const el=id=>document.getElementById(id);let rooms=[],leaving=false;
 function filter(){if(leaving)return;const query=el('search').value.trim().toLocaleLowerCase();renderRooms(el('rooms'),rooms.filter(r=>(r.name+' '+r.ownerName).toLocaleLowerCase().includes(query)),joinPublic);}
 async function refresh(){if(leaving||document.hidden)return;try{rooms=await getRooms();filter();el('lobbyStatus').textContent=rooms.length+' 个公开房间';}catch(error){if(!leaving)el('lobbyStatus').textContent=error.message+'，可以使用邀请加入。';}}
 async function enter(target){try{leaving=true;await openRoom(target);}catch(error){leaving=false;el('joinStatus').textContent=error.message;}}
 function joinPublic(id){void enter(publicLink(id));}
 el('search').addEventListener('input',filter);el('refresh').onclick=()=>void refresh();
 function open(){try{void enter(invitationTarget(el('invitation').value,location.origin));}catch(error){el('joinStatus').textContent=error.message;}}
 el('openRoom').onclick=open;el('invitation').onkeydown=event=>{if(event.key==='Enter')open();};
 el('create').onclick=()=>{el('createNickname').value=readPreferences().nickname;el('createName').value=readPreferences().nickname+'的房间';el('createPublic').checked=false;el('createError').textContent='';el('createDialog').showModal();};
 el('cancelCreate').onclick=()=>el('createDialog').close();
 el('createForm').onsubmit=async event=>{
  event.preventDefault();const submit=el('submitCreate');const nickname=cleanNickname(el('createNickname').value),name=el('createName').value.trim();if(!nickname||!name){el('createError').textContent='填写昵称和房间名后即可创建。';return;}el('submitCreate').disabled=true;
  try{const bytes=crypto.getRandomValues(new Uint8Array(32)),key=[...bytes].map(n=>n.toString(16).padStart(2,'0')).join(''),isPublic=el('createPublic').checked;
   const response=await fetch('/api/rooms',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name,public:isPublic,publicKey:isPublic?key:undefined})});
   if(!response.ok)throw Error(response.status===429?'当前房间已满或创建较频繁，请稍后重试':'房间暂时无法创建');const room=await response.json();savePreferences({nickname});sessionStorage.setItem('voicebridge-owner:'+room.id,room.writeToken);el('createDialog').close();await enter('/room/'+room.id+'#'+room.listenToken+'.'+key);
  }catch(error){el('createError').textContent=error.message;}finally{submit.disabled=false;}
 };
 void refresh();setInterval(()=>void refresh(),10000);
}
