import {currentUser} from './auth-client.js';
export async function openRoom(target){
 const url=new URL(target,location.origin);
 if(url.origin!==location.origin||!/^\/rooms?\/[a-f0-9]{32}$/.test(url.pathname))throw Error('房间地址无效');
 try{await currentUser();}catch(error){if(error.status!==401)throw error;try{sessionStorage.setItem('voicebridge.return',url.pathname+url.hash);}catch{}location.assign('/login');return;}
 location.assign(url.pathname+url.hash);
}
