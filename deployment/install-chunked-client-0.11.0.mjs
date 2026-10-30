import fs from 'node:fs/promises';import path from 'node:path';import {createHash,randomBytes} from 'node:crypto';import {gunzipSync} from 'node:zlib';
const expected='4398c1eb6e441309bbc24142bc9fa79861f12890c9381ec642ce38bc58b4509e',expectedBytes=81983273;
if(process.getuid?.()!==0)throw Error('Run the approved deployment as root');
const root='/var/lib/voicebridge-deployment-0.11.0',target='/opt/voicebridge/downloads/VoiceBridge.exe';
const digest=data=>createHash('sha256').update(data).digest('hex');
try{const current=await fs.readFile(target);if(current.length===expectedBytes&&digest(current)===expected){console.log('PUBLIC_CLIENT_ALREADY_VERIFIED');process.exit(0);}}catch(error){if(error.code!=='ENOENT')throw error;}
await fs.mkdir(root,{recursive:true,mode:0o700});const state=await fs.lstat(root);if(!state.isDirectory()||state.isSymbolicLink()||state.uid!==0||(state.mode&0o077))throw Error('Unsafe deployment cache permissions');
const base='https://api.github.com/repos/zhangmingfeng962-cyber/VoiceBridge/';
async function download(url,accept,max){
 const response=await fetch(url,{headers:{Accept:accept,'User-Agent':'VoiceBridge-verified-deployment'},signal:AbortSignal.timeout(90000)});
 if(!response.ok)throw Error('GitHub artifact HTTP '+response.status);
 const parts=[];let bytes=0;for await(const part of response.body){bytes+=part.length;if(bytes>max)throw Error('Artifact response exceeds expected limit');parts.push(part);}
 return Buffer.concat(parts);
}
const api=JSON.parse((await download(base+'contents/deployment/client-transfer-0.11.0.json?ref='+encodeURIComponent(process.argv[2]||'main'),'application/vnd.github+json',65536)).toString());
if(api.encoding!=='base64')throw Error('Metadata encoding invalid');
const meta=JSON.parse(Buffer.from(api.content,'base64').toString());
if(meta.encoding!=='chunks-gzip-v1'||meta.version!=='0.11.0'||meta.exeSha256!==expected||meta.bytes!==expectedBytes||!Array.isArray(meta.chunks)||meta.chunks.length<1||meta.chunks.length>40||!/^[a-f0-9]{64}$/.test(meta.gzipSha256)||!Number.isSafeInteger(meta.gzipBytes)||meta.gzipBytes<1||meta.gzipBytes>expectedBytes)throw Error('Artifact manifest invalid');
for(const chunk of meta.chunks)if(!/^[a-f0-9]{40}$/.test(chunk.blob)||!/^[a-f0-9]{64}$/.test(chunk.sha256)||!Number.isSafeInteger(chunk.bytes)||chunk.bytes<1||chunk.bytes>2097152)throw Error('Artifact chunk invalid');
if(meta.chunks.reduce((n,c)=>n+c.bytes,0)!==meta.gzipBytes)throw Error('Artifact manifest size mismatch');
let next=0;
async function worker(){
 while(next<meta.chunks.length){const index=next++,chunk=meta.chunks[index],file=path.join(root,'chunk-'+index);
  let data;try{data=await fs.readFile(file);}catch(error){if(error.code!=='ENOENT')throw error;}
  if(!data||data.length!==chunk.bytes||digest(data)!==chunk.sha256){
   data=await download(base+'git/blobs/'+chunk.blob,'application/vnd.github.raw+json',4194304);
   if(data.length!==chunk.bytes){try{const result=JSON.parse(data.toString());if(result.encoding==='base64')data=Buffer.from(result.content,'base64');}catch{}}
   if(data.length!==chunk.bytes||digest(data)!==chunk.sha256)throw Error('Artifact chunk integrity mismatch '+index);
   await fs.writeFile(file,data,{mode:0o600});
  }
  console.log('CLIENT_CHUNK_VERIFIED',index+1,meta.chunks.length);
 }
}
await Promise.all([worker(),worker(),worker()]);
const packed=Buffer.concat(await Promise.all(meta.chunks.map((_,i)=>fs.readFile(path.join(root,'chunk-'+i)))));
if(packed.length!==meta.gzipBytes||digest(packed)!==meta.gzipSha256)throw Error('Compressed artifact integrity mismatch');
const binary=gunzipSync(packed,{maxOutputLength:expectedBytes});
if(binary.length!==expectedBytes||digest(binary)!==expected)throw Error('Client integrity mismatch');
await fs.mkdir(path.dirname(target),{recursive:true,mode:0o755});
await fs.mkdir('/opt/voicebridge/download-backups',{recursive:true,mode:0o700});
try{await fs.copyFile(target,'/opt/voicebridge/download-backups/VoiceBridge-before-0.11.0.exe',fs.constants.COPYFILE_EXCL);await fs.chmod('/opt/voicebridge/download-backups/VoiceBridge-before-0.11.0.exe',0o600);}catch(error){if(!['EEXIST','ENOENT'].includes(error.code))throw error;}
const incoming=path.join(path.dirname(target),'.VoiceBridge-'+randomBytes(8).toString('hex')+'.new');
await fs.writeFile(incoming,binary,{mode:0o644,flag:'wx'});await fs.rename(incoming,target);
console.log('PUBLIC_CLIENT_INSTALLED');console.log('CLIENT_SHA256 '+expected);console.log('CLIENT_BYTES '+binary.length);

