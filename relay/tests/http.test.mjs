import {test} from 'node:test';import assert from 'node:assert/strict';import {createHash} from 'node:crypto';import {startServer} from '../server.mjs';
test('real HTTP server supports authenticated upload, read and revoke without disk storage',async()=>{const master='b'.repeat(64),s=await startServer({port:0,host:'127.0.0.1',publishKeyHash:createHash('sha256').update(master).digest('hex')});try{
  assert.equal((await(await fetch(s.url+'/health')).json()).ready,true);
  const create=await fetch(s.url+'/api/rooms',{method:'POST',headers:{Authorization:'Bearer '+master},body:JSON.stringify({player:'test'})});assert.equal(create.status,201);const room=await create.json();
  const bytes=new Uint8Array(154276);new DataView(bytes.buffer).setUint32(0,0x31454256,true);new DataView(bytes.buffer).setUint32(4,0,true);bytes[100]=34;
  const path=s.url+'/api/rooms/'+room.id;
  assert.equal((await fetch(path+'/chunks/0',{method:'PUT',headers:{Authorization:'Bearer '+room.writeToken},body:bytes})).status,200);
  const received=await fetch(path+'/next',{headers:{Authorization:'Bearer '+room.listenToken}});assert.equal(received.status,200);assert.deepEqual(new Uint8Array(await received.arrayBuffer()),bytes);
  assert.equal((await fetch(path,{method:'DELETE',headers:{Authorization:'Bearer '+room.writeToken}})).status,200);assert.equal(s.bucket.items.size,0);
  assert.equal((await fetch(path+'/next',{headers:{Authorization:'Bearer '+room.listenToken}})).status,404);
}finally{await s.stop();}});
