export function invitationTarget(value,origin){
  const input=String(value).trim();
  let url;try{url=new URL(input);}catch{throw new Error('请粘贴完整的房间邀请，包括 # 后面的内容。');}
  if(url.origin!==origin||url.username||url.password||url.search||!/^\/room\/[a-f0-9]{32}$/.test(url.pathname)||!/^#[a-f0-9]{64}\.[a-f0-9]{64}$/.test(url.hash))throw new Error('邀请格式不正确。请让朋友点击“邀请朋友”重新复制，专属控制页不能用作邀请。');
  return url.pathname+url.hash;
}
if(typeof document!=='undefined'){
  const input=document.getElementById('invitation'),status=document.getElementById('joinStatus');
  function open(){try{location.assign(invitationTarget(input.value,location.origin));}catch(error){status.textContent=error.message;input.focus();}}
  document.getElementById('openRoom').addEventListener('click',open);
  input.addEventListener('keydown',event=>{if(event.key==='Enter')open();});
}
