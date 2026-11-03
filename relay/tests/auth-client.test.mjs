import test from 'node:test';import assert from 'node:assert/strict';import http from 'node:http';
import {accountApi} from '../web/auth-client.js';

test('account requests recover from stalled headers and bodies instead of waiting forever',{timeout:40000},async()=>{
 const server=http.createServer((req,res)=>{if(req.url==='/body'){res.writeHead(200,{'Content-Type':'application/json'});res.flushHeaders();return;}if(req.url==='/ok'){res.writeHead(200,{'Content-Type':'application/json'});res.end('{"ready":true}');}});
 await new Promise(r=>server.listen(0,'127.0.0.1',r));const origin='http://127.0.0.1:'+server.address().port;
 try{await Promise.all(['/headers','/body'].map(route=>assert.rejects(accountApi(origin+route,{}),/网络响应超时/)));assert.deepEqual(await accountApi(origin+'/ok',{}),{ready:true});}
 finally{server.closeAllConnections();await new Promise(r=>server.close(r));}
});
