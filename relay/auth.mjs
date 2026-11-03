import {Worker} from 'node:worker_threads';
import {randomBytes,createHash} from 'node:crypto';
import {isIP} from 'node:net';
import path from 'node:path';
import {readFile} from 'node:fs/promises';
import {configuredMailer} from './mail.mjs';

export async function createAuth({directory=process.env.AUTH_DATA_ROOT||'/var/lib/voicebridge/accounts',sendMail=configuredMailer(),freeRemaining=Number(process.env.MAIL_FREE_REMAINING||0),dailyLimit=Number(process.env.MAIL_DAILY_LIMIT||180),onChange=()=>{},authorizeAttachment=()=>false}={}){
 if(!Number.isSafeInteger(freeRemaining)||freeRemaining<0||!Number.isFinite(dailyLimit)||dailyLimit<1||dailyLimit>180)throw Error('Invalid finite mail quota');
 const worker=new Worker(new URL('./auth-store.worker.mjs',import.meta.url),{workerData:{directory,freeRemaining,dailyLimit},resourceLimits:{maxOldGenerationSizeMb:128}}),pending=new Map(),attachments=new Map();let next=0,readyResolve,readyReject,failed;
 const ready=new Promise((resolve,reject)=>{readyResolve=resolve;readyReject=reject;});
 worker.on('message',message=>{if(message.ready){readyResolve();return;}const job=pending.get(message.id);if(!job)return;pending.delete(message.id);if(message.error)job.reject(Object.assign(Error(message.error.message),{status:message.error.status}));else job.resolve(message.result);});
 const fail=error=>{failed=Error('账号服务暂时不可用');readyReject(error);for(const job of pending.values())job.reject(failed);pending.clear();};worker.on('error',fail);worker.on('exit',code=>{if(code)fail(Error('Account worker stopped'));});await ready;
 const rpc=(operation,args={})=>{if(failed)return Promise.reject(failed);if(pending.size>=16)return Promise.reject(Object.assign(Error('账号服务繁忙，请稍后再试'),{status:429}));return new Promise((resolve,reject)=>{const id=++next;pending.set(id,{resolve,reject});worker.postMessage({id,operation,args});});};
 const token=req=>{const bearer=/^Bearer ([a-f0-9]{64})$/.exec(req.headers.authorization||'');if(bearer)return bearer[1];return /(?:^|;\s*)__Host-vb_session=([a-f0-9]{64})(?:;|$)/.exec(req.headers.cookie||'')?.[1]||null;};
 const authenticate=req=>rpc('who',{token:token(req)});
 const address=req=>{const remote=req.socket.remoteAddress||'',local=['127.0.0.1','::1','::ffff:127.0.0.1'].includes(remote),forwarded=req.headers['x-real-ip'];return local&&typeof forwarded==='string'&&isIP(forwarded)?forwarded:remote;};
 const readBody=async req=>{let size=0;const chunks=[];for await(const c of req){size+=c.length;if(size>1500000)throw Object.assign(Error('请求内容过大'),{status:413});chunks.push(c);}try{return JSON.parse(Buffer.concat(chunks).toString()||'{}');}catch{throw Object.assign(Error('请求格式错误'),{status:400});}};
 const cookie=(value,expires)=>'__Host-vb_session='+value+'; Path=/; HttpOnly; Secure; SameSite=Lax; Max-Age='+Math.max(0,Math.floor((expires-Date.now())/1000));
 function identityChanged(result){for(const [key,value] of attachments)if(result.sessionHash===value.sessionHash||result.revokedUserId===value.userId&&value.sessionHash!==result.keepSessionHash)attachments.delete(key);if(result.user||result.revokedUserId||result.sessionHash)onChange(result);return result;}
 async function route(req,res,url,reply){
  const p=url.pathname;if(p.startsWith('/avatars/')){if(req.method!=='GET'||!/^\/avatars\/[a-f0-9]{64}\.png$/.test(p)){reply(res,404,{error:'Not found'});return true;}try{const bytes=await readFile(path.join(directory,'avatars',p.split('/').pop()));res.writeHead(200,{'Content-Type':'image/png','Cache-Control':'public,max-age=31536000,immutable','X-Content-Type-Options':'nosniff'});res.end(bytes);}catch{reply(res,404,{error:'Not found'});}return true;}
  if(!p.startsWith('/api/auth/')&&p!=='/api/me'&&p!=='/api/me/avatar')return false;
  try{
   if(p==='/api/me'&&req.method==='GET'){const identity=await authenticate(req);if(!identity)throw Object.assign(Error('请先登录'),{status:401});reply(res,200,{user:identity.user,expires:identity.expires});return true;}
   if(!['POST','PATCH'].includes(req.method)){reply(res,405,{error:'Method not allowed'});return true;}
   const body=await readBody(req),ip=address(req),session=token(req);let result;
   if(p==='/api/auth/code'){if(!sendMail)throw Object.assign(Error('注册邮件服务尚未启用；已有账号可以使用密码登录'),{status:503});const mail=await rpc('requestCode',{...body,ip});try{await sendMail(mail);}catch{await rpc('cancelCode',{address:mail.address,purpose:mail.purpose});throw Object.assign(Error('邮件发送失败，请稍后重试'),{status:503});}reply(res,200,{sent:true,retryAfter:60});return true;}
   if(p==='/api/auth/verify')result=await rpc('verifyCode',{...body,ip});
   else if(p==='/api/auth/register')result=await rpc('register',body);
   else if(p==='/api/auth/login')result=await rpc('login',{...body,ip});
   else if(p==='/api/auth/refresh')result=await rpc('refresh',{token:session});
   else if(p==='/api/auth/logout'){result=identityChanged(await rpc('logout',{token:session}));res.setHeader('Set-Cookie',cookie('',0));result={loggedOut:true};}
   else if(p==='/api/auth/reset'){identityChanged(await rpc('reset',body));result={reset:true};}
   else if(p==='/api/auth/password'){result=identityChanged(await rpc('changePassword',{...body,token:session}));result={changed:true};}
   else if(p==='/api/me'&&req.method==='PATCH')result=identityChanged(await rpc('profile',{...body,token:session}));
   else if(p==='/api/me/avatar')result=identityChanged(await rpc('avatar',{...body,token:session}));
   else if(p==='/api/auth/desktop-ticket'){const identity=await authenticate(req);if(!identity)throw Object.assign(Error('请先登录'),{status:401});if(!/^[a-f0-9]{32}$/.test(body.roomId||'')||!/^[a-f0-9]{32}$/.test(body.memberId||''))throw Object.assign(Error('房间信息无效'),{status:400});if(!authorizeAttachment(identity,body))throw Object.assign(Error('房间成员身份无效'),{status:403});if(attachments.size>=128)throw Object.assign(Error('请稍后重试'),{status:429});const ticket=randomBytes(32).toString('hex');attachments.set(createHash('sha256').update(ticket).digest('hex'),{...identity,roomId:body.roomId,memberId:body.memberId,expires:Date.now()+30000,scope:'controller'});result={ticket};}
   else{reply(res,404,{error:'Not found'});return true;}
   if(result.token){res.setHeader('Set-Cookie',cookie(result.token,result.expires));if(req.headers['x-voicebridge-client']!=='desktop'){result={user:result.user,expires:result.expires};}}
   reply(res,200,result);return true;
  }catch(error){reply(res,error.status||500,{error:error.status?error.message:'账号服务暂时不可用'});return true;}
 }
 function consumeAttachment(ticket){if(typeof ticket!=='string'||!/^[a-f0-9]{64}$/.test(ticket))return null;const key=createHash('sha256').update(ticket).digest('hex'),value=attachments.get(key);attachments.delete(key);return value?.expires>Date.now()?value:null;}
 const cleanup=setInterval(()=>{void rpc('cleanup').catch(()=>{});for(const [key,value] of attachments)if(value.expires<Date.now())attachments.delete(key);},60000);cleanup.unref();
 return {route,authenticate,token,rpc,consumeAttachment,stop:async()=>{clearInterval(cleanup);await worker.terminate();}};
}
