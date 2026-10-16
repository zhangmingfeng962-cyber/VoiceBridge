import http from 'node:http';
import {readFile} from 'node:fs/promises';
import {Readable} from 'node:stream';
import {pathToFileURL} from 'node:url';
import worker,{setAssets} from './worker/index.js';

// A single-process, bounded, RAM-only encrypted live buffer. No database or file recordings.
export class MemoryBucket {
  constructor(){this.items=new Map();this.counter=0;}
  async put(key,value,options={}) {
    if(options.onlyIf&&this.items.get(key)?.etag!==options.onlyIf.etagMatches)return null;
    const bytes=typeof value==='string'?new TextEncoder().encode(value):new Uint8Array(value).slice();
    if(bytes.length>160000)throw new Error('Object too large');
    if(!this.items.has(key)&&this.items.size>=100)throw new Error('Live buffer full');
    const item={bytes,etag:String(++this.counter)};this.items.set(key,item);return {etag:item.etag};
  }
  async get(key){const item=this.items.get(key);if(!item)return null;return {etag:item.etag,body:item.bytes.slice(),json:async()=>JSON.parse(new TextDecoder().decode(item.bytes))};}
  async delete(keys){for(const key of Array.isArray(keys)?keys:[keys])this.items.delete(key);}
  async list({prefix,limit}){return {objects:[...this.items.keys()].filter(key=>key.startsWith(prefix)).slice(0,limit).map(key=>({key}))};}
  expire(){const now=Date.now();for(const [key,item] of this.items){if(!key.endsWith('/meta'))continue;const meta=JSON.parse(new TextDecoder().decode(item.bytes));if(meta.expiresAt<now||meta.createdAt+7200000<now){for(const candidate of this.items.keys())if(candidate.startsWith(`rooms/${meta.id}/`))this.items.delete(candidate);}}}
}
export async function startServer({port=Number(process.env.PORT||3000),host='0.0.0.0',origin=process.env.RENDER_EXTERNAL_URL||process.env.PUBLIC_ORIGIN||null,publishKeyHash=process.env.PUBLISH_KEY_HASH}={}) {
  if(!/^[a-f0-9]{64}$/.test(publishKeyHash||''))throw new Error('Configure PUBLISH_KEY_HASH before starting; never put the sender key in source.');
  const assets={};for(const file of ['receiver.html','receiver.js','pcm-worklet.js'])assets[file]=await readFile(new URL('./web/'+file,import.meta.url),'utf8');setAssets(assets);
  const bucket=new MemoryBucket(),env={BUCKET:bucket,PUBLISH_KEY_HASH:publishKeyHash};
  const watchers=new Map();
  const notify=id=>{for(const finish of [...(watchers.get(id)||[])])finish();};
  const watch=(id,res)=>{
    let finish;const callbacks=watchers.get(id)||new Set();watchers.set(id,callbacks);
    const promise=new Promise(resolve=>{finish=resolve;});
    const timer=setTimeout(()=>finish(),10000);timer.unref();callbacks.add(finish);res.once('close',finish);
    return {promise,dispose(){clearTimeout(timer);callbacks.delete(finish);if(!callbacks.size)watchers.delete(id);res.off('close',finish);}};
  };
  const server=http.createServer(async(req,res)=>{
    try {
      if(Number(req.headers['content-length'])>160000){res.writeHead(413);res.end();return;}
      const base=origin||`http://127.0.0.1:${server.address().port}`;
      const requestUrl=new URL(req.url,base);
      const input=new Request(requestUrl,{method:req.method,headers:req.headers,...(!['GET','HEAD'].includes(req.method)?{body:Readable.toWeb(req),duplex:'half'}:{})});
      const room=requestUrl.pathname.match(/^\/api\/rooms\/([a-f0-9]{32})/),waiting=requestUrl.pathname.endsWith('/next')&&req.method==='GET'&&room?watch(room[1],res):null;
      const tasks=[],ctx={waitUntil(task){tasks.push(task);}};let response;
      try {
        response=await worker.fetch(input,env,ctx);
        if(waiting&&response.status===204){await waiting.promise;if(res.destroyed)return;response=await worker.fetch(input,env,ctx);}
      }finally{waiting?.dispose();}
      if(room&&response.ok&&['PUT','DELETE'].includes(req.method))notify(room[1]);
      if(res.destroyed)return;
      res.writeHead(response.status,Object.fromEntries(response.headers));
      if(response.body)Readable.fromWeb(response.body).pipe(res);else res.end();
      void Promise.allSettled(tasks);
    }catch{if(!res.headersSent)res.writeHead(503,{'Cache-Control':'no-store'});res.end();}
  });
  server.headersTimeout=10000;server.requestTimeout=15000;server.maxConnections=64;
  await new Promise((resolve,reject)=>{server.once('error',reject);server.listen(port,host,resolve);});
  const cleanup=setInterval(()=>bucket.expire(),30000);cleanup.unref();
  return {server,bucket,url:`http://127.0.0.1:${server.address().port}`,stop:async()=>{clearInterval(cleanup);for(const id of watchers.keys())notify(id);server.closeAllConnections();await new Promise(resolve=>server.close(resolve));bucket.items.clear();}};
}
if(process.argv[1]&&import.meta.url===pathToFileURL(process.argv[1]).href){const running=await startServer();console.log('VoiceBridge encrypted relay is listening');for(const signal of ['SIGTERM','SIGINT'])process.once(signal,()=>{void running.stop().then(()=>process.exit());});}
