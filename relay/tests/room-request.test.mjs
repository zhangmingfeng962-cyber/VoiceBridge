import test from 'node:test';import assert from 'node:assert/strict';import http from 'node:http';import {roomRequest} from '../web/lobby.js';
test('room recovery requests stop waiting for a stalled response and remain retryable',{timeout:10000},async()=>{
 const server=http.createServer((req,res)=>{res.writeHead(200,{'Content-Type':'application/json'});if(req.url==='/stall'){res.flushHeaders();return;}res.end('{"exists":true}');});await new Promise(r=>server.listen(0,'127.0.0.1',r));const origin='http://127.0.0.1:'+server.address().port;
 try{await assert.rejects(roomRequest(origin+'/stall'),/网络响应超时/);assert.deepEqual((await roomRequest(origin+'/ok')).data,{exists:true});}finally{server.closeAllConnections();await new Promise(r=>server.close(r));}
});
