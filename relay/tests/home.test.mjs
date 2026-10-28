import test from 'node:test';
import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
import {startServer} from './test-server.mjs';
import {invitationTarget} from '../web/home.js';
test('public landing is separate from invited room and displays the registered ICP link',async()=>{
  const service=await startServer({port:0,host:'127.0.0.1',publishKeyHash:createHash('sha256').update('test').digest('hex')});
  try{
    const home=await fetch(service.url+'/'),text=await home.text();
    assert.equal(home.status,200);assert.match(text,/下载 Windows 客户端/);assert.match(text,/渝ICP备2026026961号-1/);assert.match(text,/https:\/\/beian.miit.gov.cn\//);assert.doesNotMatch(text,/id="screenStart"/);
    const room=await (await fetch(service.url+'/room/'+'a'.repeat(32))).text();assert.match(room,/id="screenStart"/);assert.match(room,/渝ICP备2026026961号-1/);
    assert.equal((await fetch(service.url+'/home.js')).headers.get('content-type'),'text/javascript; charset=utf-8');
  }finally{await service.stop();}
});
test('landing only opens complete invitations for this origin and rejects control links',()=>{
  const origin='https://voicebridge-room.cn',path='/room/'+'a'.repeat(32),hash='#'+'b'.repeat(64)+'.'+'c'.repeat(64),valid=origin+path+hash;
  assert.equal(invitationTarget(valid,origin),path+hash);
  for(const value of [valid.replace(origin,'https://evil.example'),valid+'.desktop.x',valid.replace('#','?tracking=1#'),valid.replace('https:','http:'),'javascript:alert(1)',origin+path,valid.replace(origin,'https://user:pass@voicebridge-room.cn')])assert.throws(()=>invitationTarget(value,origin));
});
