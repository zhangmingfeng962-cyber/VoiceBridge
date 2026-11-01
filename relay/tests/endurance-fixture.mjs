import http from 'node:http';import {createHash} from 'node:crypto';import {startServer} from '../server.mjs';
const service=await startServer({host:'127.0.0.1',port:4094,publishKeyHash:createHash('sha256').update('1'.repeat(64)).digest('hex')});
const control=http.createServer((req,res)=>{if(req.method==='POST'&&req.url==='/drop'){for(const peer of service.peers.values())if(peer.role==='desktop')peer.socket.terminate();res.writeHead(200);res.end('{}');return;}res.writeHead(404);res.end();});
await new Promise(resolve=>control.listen(4095,'127.0.0.1',resolve));console.log('ENDURANCE_FIXTURE_READY');
let closing=false;async function close(){if(closing)return;closing=true;await service.stop();control.close();}
process.on('SIGINT',()=>void close());process.on('SIGTERM',()=>void close());
