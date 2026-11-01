export async function openRoom(target){
 const url=new URL(target,location.origin);
 if(url.origin!==location.origin||!/^\/rooms?\/[a-f0-9]{32}$/.test(url.pathname))throw Error('房间地址无效');
 const response=await fetch(url.pathname,{cache:'no-store'});if(!response.ok)throw Error('无法打开房间');
 const parsed=new DOMParser().parseFromString(await response.text(),'text/html'),main=parsed.querySelector('main');if(!main)throw Error('房间页面暂不可用');
 let stylesheet=document.querySelector('link[href="/room.css"]');if(!stylesheet){stylesheet=document.createElement('link');stylesheet.rel='stylesheet';stylesheet.href='/room.css';await new Promise((resolve,reject)=>{stylesheet.onload=resolve;stylesheet.onerror=()=>reject(Error('无法加载房间界面'));document.head.append(stylesheet);});}
 document.querySelector('link[href="/home.css"]')?.remove();document.body.replaceChildren(document.importNode(main,true));history.pushState({},'',url.pathname+url.hash);document.title='VoiceBridge · 音乐房间';
 const view=await import('/room.js?view='+crypto.randomUUID());await view.join();
}
