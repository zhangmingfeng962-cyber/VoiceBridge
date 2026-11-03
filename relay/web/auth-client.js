export async function accountApi(route,body,method='POST'){
 const controller=new AbortController(),timer=setTimeout(()=>controller.abort(),30000);
 try{
  const response=await fetch(route,{method,signal:controller.signal,credentials:'same-origin',headers:{'Content-Type':'application/json'},...(method==='GET'?{}:{body:JSON.stringify(body||{})})});
  const data=await response.json().catch(()=>{throw Error('服务返回异常，请稍后重试');});if(!response.ok)throw Object.assign(Error(data.error||'请求未完成'),{status:response.status});return data;
 }catch(error){if(controller.signal.aborted)throw Error('网络响应超时，请检查网络后重试；已创建的账号可以直接密码登录。');throw error;}
 finally{clearTimeout(timer);}
}
export async function currentUser(){const identity=await accountApi('/api/me',null,'GET');if(identity.expires&&identity.expires-Date.now()<7*86400000)await accountApi('/api/auth/refresh');return identity.user;}
export function rememberReturn(){try{sessionStorage.setItem('voicebridge.return',location.pathname+location.hash);}catch{}}
export function loginRedirect(){rememberReturn();location.assign('/login');}
export async function requireLogin(){try{return await currentUser();}catch(error){if(error.status===401){loginRedirect();return null;}throw error;}}
export function returnTarget(){let value;try{value=sessionStorage.getItem('voicebridge.return');sessionStorage.removeItem('voicebridge.return');}catch{}if(typeof value==='string'&&/^\/rooms?\/[a-f0-9]{32}(?:#[a-f0-9]{64}\.[a-f0-9]{64}(?:\.desktop\.[a-f0-9]{32}\.[a-f0-9]{64})?)?$/.test(value))return value;return '/';}
export async function avatarPng(file){
 if(!file||file.size>8*1024*1024||!['image/png','image/jpeg','image/webp'].includes(file.type))throw Error('请选择小于 8MB 的 PNG、JPG 或 WebP 图片');
 const bitmap=await createImageBitmap(file),canvas=document.createElement('canvas');canvas.width=canvas.height=256;const context=canvas.getContext('2d'),side=Math.min(bitmap.width,bitmap.height);context.drawImage(bitmap,(bitmap.width-side)/2,(bitmap.height-side)/2,side,side,0,0,256,256);bitmap.close();return canvas.toDataURL('image/png').split(',')[1];
}
export function profileImage(user){const image=document.createElement('img');image.className='profileAvatar';image.alt='';if(user.avatarUrl)image.src=user.avatarUrl;else{image.hidden=true;}return image;}

export function announceAccount(userId){try{const channel=new BroadcastChannel('voicebridge.account');channel.postMessage({userId});channel.close();}catch{}}
