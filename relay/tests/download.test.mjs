import test from 'node:test';import assert from 'node:assert/strict';import fs from 'node:fs/promises';import os from 'node:os';import path from 'node:path';
import {startDownloads,transferRate} from '../download-server.mjs';
test('download budget uses spare capacity and degrades safely on stale audio metrics',()=>{
 assert.equal(transferRate({at:1000,audioBits:0,screenBits:0},1001),1048576);assert.equal(transferRate({at:1000,audioBits:2000000,screenBits:5000000},1001),250000);assert.equal(transferRate({at:1000,audioBits:15000000,screenBits:0},1001),32768);assert.equal(transferRate({at:1000},7000),131072);
});
test('file downloads support exact ranges, reject traversal and share one rate budget',async()=>{
 const root=await fs.mkdtemp(path.join(os.tmpdir(),'voicebridge-download-test-')),bytes=Buffer.alloc(262144,71);await fs.writeFile(path.join(root,'VoiceBridgeSetup.exe'),bytes);
 const service=await startDownloads({port:0,downloadRoot:root,updateRoot:root,fixedRate:262144});
 try{
  const head=await fetch(service.url+'/downloads/VoiceBridgeSetup.exe',{method:'HEAD'});assert.equal(Number(head.headers.get('content-length')),bytes.length);assert.equal(head.headers.get('cache-control'),'no-cache');
  const range=await fetch(service.url+'/downloads/VoiceBridgeSetup.exe',{headers:{Range:'bytes=12-43'}});assert.equal(range.status,206);assert.deepEqual(Buffer.from(await range.arrayBuffer()),bytes.subarray(12,44));
  assert.equal((await fetch(service.url+'/downloads/VoiceBridgeSetup.exe',{headers:{Range:'bytes=999999-'}})).status,416);assert.equal((await fetch(service.url+'/downloads/%2e%2e/secret')).status,404);
  const start=Date.now();const results=await Promise.all([fetch(service.url+'/downloads/VoiceBridgeSetup.exe').then(r=>r.arrayBuffer()),fetch(service.url+'/downloads/VoiceBridgeSetup.exe').then(r=>r.arrayBuffer())]);
  assert.ok(Date.now()-start>1400,'two clients must share the aggregate budget');assert.deepEqual(Buffer.from(results[0]),bytes);assert.deepEqual(Buffer.from(results[1]),bytes);
 }finally{await service.stop();await fs.rm(root,{recursive:true,force:true});}
});
