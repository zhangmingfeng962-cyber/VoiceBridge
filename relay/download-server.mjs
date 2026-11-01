import http from 'node:http';import fs from 'node:fs/promises';import path from 'node:path';import {pathToFileURL} from 'node:url';
export function transferRate(metrics,now=Date.now()){
 if(!metrics||now-metrics.at>5000)return 131072;
 const available=Math.max(0,10000000-1000000-Math.max(0,metrics.audioBits||0)-Math.max(0,metrics.screenBits||0));
 return Math.max(32768,Math.min(1048576,Math.floor(available/8)));
}
export async function startDownloads({port=3101,host='127.0.0.1',downloadRoot='/opt/voicebridge/downloads',updateRoot='/opt/voicebridge/updates',metricsFile='/run/voicebridge/traffic.json',fixedRate}={}){
 let metrics=null,tokens=0,previous=Date.now(),next=0,closing=false;const jobs=[];
 const refresh=setInterval(async()=>{try{metrics=JSON.parse(await fs.readFile(metricsFile,'utf8'));}catch{metrics=null;}},500);refresh.unref();
 const server=http.createServer(async(req,res)=>{
  try{
   if(!['GET','HEAD'].includes(req.method)){res.writeHead(405);res.end();return;}
   const url=new URL(req.url,'http://localhost'),match=url.pathname.match(/^\/(downloads|updates)\/([A-Za-z0-9._-]+)$/);
   if(!match||match[2].includes('..')){res.writeHead(404);res.end();return;}
   const name=match[2],root=path.resolve(match[1]==='updates'?updateRoot:downloadRoot),file=path.resolve(root,name);
   if(!file.startsWith(root+path.sep)){res.writeHead(404);res.end();return;}const stat=await fs.lstat(file);if(!stat.isFile()||stat.isSymbolicLink()){res.writeHead(404);res.end();return;}
   const etag='"'+stat.size+'-'+Math.floor(stat.mtimeMs)+'"',headers={'Accept-Ranges':'bytes','ETag':etag,'Cache-Control':(/\.(json|sig|exe)$/.test(name))?'no-cache':'public, max-age=31536000, immutable','X-Content-Type-Options':'nosniff','Content-Type':name.endsWith('.json')?'application/json':'application/octet-stream'};
   if(name.endsWith('.exe'))headers['Content-Disposition']='attachment; filename="'+name+'"';
   if(req.headers['if-none-match']===etag&&!req.headers.range){res.writeHead(304,headers);res.end();return;}
   let start=0,end=stat.size-1,status=200;
   if(req.headers.range&&(!req.headers['if-range']||req.headers['if-range']===etag)){
    const range=/^bytes=(\d+)-(\d*)$/.exec(req.headers.range);if(!range){res.writeHead(416,{'Content-Range':'bytes */'+stat.size});res.end();return;}
    start=Number(range[1]);end=range[2]?Math.min(Number(range[2]),end):end;if(!Number.isSafeInteger(start)||start>end||start>=stat.size){res.writeHead(416,{'Content-Range':'bytes */'+stat.size});res.end();return;}status=206;headers['Content-Range']='bytes '+start+'-'+end+'/'+stat.size;
   }
   headers['Content-Length']=end-start+1;
   if(req.method==='HEAD'){res.writeHead(status,headers);res.end();return;}
   if(jobs.length>=24){res.writeHead(503,{'Retry-After':'10'});res.end();return;}
   res.writeHead(status,headers);
   if(match[1]==='updates'&&/\.(json|sig)$/.test(name)&&stat.size<65536&&!req.headers.range){const handle=await fs.open(file,'r');try{const buffer=Buffer.alloc(end-start+1);const result=await handle.read(buffer,0,buffer.length,start);res.end(buffer.subarray(0,result.bytesRead));}finally{await handle.close();}return;}
   const handle=await fs.open(file,'r'),job={res,handle,position:start,end,busy:false,closed:false};jobs.push(job);
   res.on('close',()=>{job.closed=true;if(!job.busy)void handle.close().catch(()=>{});});
  }catch(error){if(!res.headersSent)res.writeHead(error.code==='ENOENT'?404:500);res.end();}
 });
 const pump=setInterval(async()=>{
  const now=Date.now(),rate=fixedRate??transferRate(metrics,now);tokens=Math.min(rate/4,tokens+rate*(now-previous)/1000);previous=now;
  for(let i=jobs.length-1;i>=0;i--)if(jobs[i].closed&&!jobs[i].busy)jobs.splice(i,1);
  if(!jobs.length||tokens<4096||closing)return;
  for(let count=0;count<jobs.length&&tokens>=4096;count++){
   const job=jobs[next++%jobs.length];if(job.busy||job.closed||job.res.writableNeedDrain)continue;
   const size=Math.min(32768,Math.floor(tokens),job.end-job.position+1);if(size<=0)continue;tokens-=size;job.busy=true;
   try{const buffer=Buffer.allocUnsafe(size),read=await job.handle.read(buffer,0,size,job.position);job.position+=read.bytesRead;if(!job.closed)job.res.write(buffer.subarray(0,read.bytesRead));if(!read.bytesRead||job.position>job.end){job.closed=true;job.res.end();await job.handle.close();}}
   catch{job.closed=true;job.res.destroy();await job.handle.close().catch(()=>{});}finally{job.busy=false;if(job.closed)await job.handle.close().catch(()=>{});}
  }
 },10);pump.unref();
 server.requestTimeout=0;server.maxConnections=40;await new Promise((resolve,reject)=>{server.once('error',reject);server.listen(port,host,resolve);});
 return {server,url:'http://127.0.0.1:'+server.address().port,stop:async()=>{closing=true;clearInterval(refresh);clearInterval(pump);for(const job of jobs){job.closed=true;job.res.destroy();await job.handle.close().catch(()=>{});}server.closeAllConnections();await new Promise(resolve=>server.close(resolve));}};
}
if(process.argv[1]&&import.meta.url===pathToFileURL(process.argv[1]).href){const running=await startDownloads({downloadRoot:process.env.DOWNLOAD_ROOT||undefined,updateRoot:process.env.UPDATE_ROOT||undefined,metricsFile:process.env.DOWNLOAD_METRICS_FILE||undefined});console.log('VoiceBridge download worker ready');for(const signal of ['SIGINT','SIGTERM'])process.once(signal,()=>void running.stop().then(()=>process.exit()));}
