import http from 'node:http';import {createHash} from 'node:crypto';import {startServer} from './test-server.mjs';
const service=await startServer({port:0,host:'127.0.0.1',publishKeyHash:createHash('sha256').update('1'.repeat(64)).digest('hex')});
const control=http.createServer((req,res)=>{if(req.method==='POST'&&req.url==='/drop'){for(const peer of service.peers.values())if(peer.role==='desktop')peer.socket.terminate();res.writeHead(200);res.end('{}');return;}if(req.method==='GET'&&req.url==='/stats'){res.writeHead(200,{'Content-Type':'application/json'});res.end(JSON.stringify({rooms:service.rooms.size,peers:service.peers.size}));return;}res.writeHead(404);res.end();});
await new Promise(resolve=>control.listen(0,'127.0.0.1',resolve));console.log(JSON.stringify({origin:service.url,control:'http://127.0.0.1:'+control.address().port}));
let stopped=false;async function close(){if(stopped)return;stopped=true;clearTimeout(timeout);await service.stop();control.closeAllConnections();control.close();process.exit(0);}
process.stdin.on('data',()=>void close());process.stdin.on('end',()=>void close());const timeout=setTimeout(()=>void close(),120000);
