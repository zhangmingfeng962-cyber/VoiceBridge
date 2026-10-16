const assets = {};
export function setAssets(value) { Object.assign(assets,value); }
const ENCODER = new TextEncoder();
const ROOM = /^[a-f0-9]{32}$/, TOKEN = /^[a-f0-9]{64}$/;
const KEEP = 20, CHUNK_SIZE = 154276, LEASE_MS = 120000, SESSION_MS = 7200000;
class HttpError extends Error { constructor(status,message) {super(message);this.status=status;} }
const hex = bytes => Array.from(bytes,b=>b.toString(16).padStart(2,'0')).join('');
const secret = () => hex(crypto.getRandomValues(new Uint8Array(32)));
const digest = async value => hex(new Uint8Array(await crypto.subtle.digest('SHA-256',ENCODER.encode(value))));
const equal = (a,b) => { if(typeof a!=='string'||typeof b!=='string'||a.length!==b.length)return false;let x=0;for(let i=0;i<a.length;i++)x|=a.charCodeAt(i)^b.charCodeAt(i);return x===0; };
const bearer = request => (request.headers.get('Authorization')||'').replace(/^Bearer /,'');
const key = id => `rooms/${id}/meta`;
const part = (id,seq) => `rooms/${id}/audio-${String(seq).padStart(9,'0')}`;
const headers = {'Cache-Control':'no-store','Referrer-Policy':'no-referrer','X-Content-Type-Options':'nosniff','X-Frame-Options':'DENY','Permissions-Policy':'microphone=(), camera=(), display-capture=()','Content-Security-Policy':"default-src 'none'; script-src 'self'; style-src 'unsafe-inline'; connect-src 'self'; worker-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'"};
const json = (data,status=200,extra={}) => new Response(JSON.stringify(data),{status,headers:{...headers,'Content-Type':'application/json; charset=utf-8',...extra}});
async function boundedBody(request,max) {
  if(Number(request.headers.get('Content-Length'))>max)throw new HttpError(413,'Payload too large');
  const reader=request.body?.getReader();if(!reader)return new Uint8Array();
  const pieces=[];let size=0;
  try {while(true){const {done,value}=await reader.read();if(done)break;size+=value.length;if(size>max){await reader.cancel();throw new HttpError(413,'Payload too large');}pieces.push(value);}}
  finally{reader.releaseLock();}
  const bytes=new Uint8Array(size);let offset=0;for(const piece of pieces){bytes.set(piece,offset);offset+=piece.length;}return bytes;
}
async function remove(bucket,id,meta) {
  const names=[key(id)];for(let n=Math.max(0,meta.lastSeq-KEEP);n<=meta.lastSeq;n++)names.push(part(id,n));
  await bucket.delete(names);
}
async function load(bucket,id,ctx) {
  const object=await bucket.get(key(id));if(!object)throw new HttpError(404,'Sharing stopped or invitation expired');
  const meta=await object.json();
  if(meta.expiresAt<Date.now()||meta.createdAt+SESSION_MS<Date.now()) {ctx.waitUntil(remove(bucket,id,meta));throw new HttpError(410,'Sharing expired');}
  return {object,meta};
}
async function authorize(request,expected) {
  const token=bearer(request);if(!TOKEN.test(token)||!equal(await digest(token),expected))throw new HttpError(403,'Invitation invalid');
}
async function handle(request,env,ctx) {
  const url=new URL(request.url), path=url.pathname;
  if(request.method==='GET' && (path==='/'||/^\/listen\/[a-f0-9]{32}$/.test(path)))return new Response(assets['receiver.html'],{headers:{...headers,'Content-Type':'text/html; charset=utf-8'}});
  if(request.method==='GET' && ['/receiver.js','/pcm-worklet.js'].includes(path))return new Response(assets[path.slice(1)],{headers:{...headers,'Content-Type':'text/javascript; charset=utf-8'}});
  if(path==='/health'&&request.method==='GET')return json({service:'VoiceBridge encrypted music relay',version:1,ready:!!env.BUCKET&&!!env.PUBLISH_KEY_HASH,chunkMs:400,sampleRate:48000,channels:2});
  if(!env.BUCKET||!env.PUBLISH_KEY_HASH)throw new HttpError(503,'Relay is not configured');
  const origin=request.headers.get('Origin');if(origin&&origin!==url.origin)throw new HttpError(403,'Origin rejected');
  if(path==='/api/rooms' && request.method==='POST') {
    await authorize(request,env.PUBLISH_KEY_HASH);
    let input;try{input=JSON.parse(new TextDecoder().decode(await boundedBody(request,256)));}catch(error){if(error instanceof HttpError)throw error;throw new HttpError(400,'Invalid JSON');}
    if(!['qqmusic','netease','kugou','test'].includes(input.player))throw new HttpError(400,'Unsupported player');
    const listing=await env.BUCKET.list({prefix:'rooms/',limit:256});let live=0;
    for(const item of listing.objects.filter(x=>x.key.endsWith('/meta'))) {
      const old=await env.BUCKET.get(item.key);if(!old)continue;const meta=await old.json();
      if(meta.expiresAt<Date.now()||meta.createdAt+SESSION_MS<Date.now())await remove(env.BUCKET,meta.id,meta);else live++;
    }
    if(live>=4)throw new HttpError(429,'Too many active shares');
    const id=hex(crypto.getRandomValues(new Uint8Array(16))), writeToken=secret(), listenToken=secret(), now=Date.now();
    const meta={id,writeHash:await digest(writeToken),listenHash:await digest(listenToken),player:input.player,lastSeq:-1,createdAt:now,expiresAt:now+LEASE_MS};
    await env.BUCKET.put(key(id),JSON.stringify(meta));
    return json({id,writeToken,listenToken,chunkMs:400,maxSessionSeconds:7200},201);
  }
  const match=path.match(/^\/api\/rooms\/([a-f0-9]{32})(?:\/(chunks|next)(?:\/(\d{1,9}))?)?$/);
  if(!match)throw new HttpError(404,'Not found');
  const id=match[1];if(!ROOM.test(id))throw new HttpError(404,'Not found');
  const {object,meta}=await load(env.BUCKET,id,ctx);
  if(request.method==='DELETE'&&!match[2]) {await authorize(request,meta.writeHash);await remove(env.BUCKET,id,meta);return json({stopped:true});}
  if(request.method==='PUT'&&match[2]==='chunks'&&match[3]!==undefined) {
    await authorize(request,meta.writeHash);const seq=Number(match[3]);
    if(seq===meta.lastSeq)return json({accepted:seq});
    if(seq!==meta.lastSeq+1)throw new HttpError(409,'Unexpected sequence');
    const bytes=await boundedBody(request,CHUNK_SIZE);
    if(bytes.length!==CHUNK_SIZE||new DataView(bytes.buffer,bytes.byteOffset).getUint32(0,true)!==0x31454256||new DataView(bytes.buffer,bytes.byteOffset).getUint32(4,true)!==seq)throw new HttpError(400,'Invalid encrypted chunk');
    await env.BUCKET.put(part(id,seq),bytes,{httpMetadata:{contentType:'application/octet-stream'}});
    meta.lastSeq=seq;meta.expiresAt=Date.now()+LEASE_MS;
    const saved=await env.BUCKET.put(key(id),JSON.stringify(meta),{onlyIf:{etagMatches:object.etag}});
    if(!saved)throw new HttpError(409,'Concurrent sender');
    if(seq>=KEEP)ctx.waitUntil(env.BUCKET.delete(part(id,seq-KEEP)));
    return json({accepted:seq});
  }
  if(request.method==='GET'&&match[2]==='next'&&!match[3]) {
    await authorize(request,meta.listenHash);
    const after=Number(url.searchParams.get('after')??'-1');if(!Number.isSafeInteger(after)||after< -1||after>999999999)throw new HttpError(400,'Invalid sequence');
    if(meta.lastSeq<0||after>=meta.lastSeq)return new Response(null,{status:204,headers:{...headers,'Retry-After':'0.2'}});
    const seq=after===-1?Math.max(0,meta.lastSeq-1):Math.max(after+1,meta.lastSeq-KEEP+1);
    const chunk=await env.BUCKET.get(part(id,seq));if(!chunk)return new Response(null,{status:204,headers});
    return new Response(chunk.body,{headers:{...headers,'Content-Type':'application/octet-stream','X-Sequence':String(seq),'X-Latest-Sequence':String(meta.lastSeq),'X-Player':meta.player}});
  }
  throw new HttpError(405,'Method not allowed');
}
export default {async fetch(request,env,ctx) {try{return await handle(request,env,ctx);}catch(error){return json({error:error instanceof HttpError?error.message:'Relay temporarily unavailable'},error instanceof HttpError?error.status:503);}}};
