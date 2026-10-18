import fs from 'node:fs';import path from 'node:path';import zlib from 'node:zlib';import crypto from 'node:crypto';import {fileURLToPath} from 'node:url';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..'),destination=path.resolve(process.argv[2]||'artifacts/offline-relay'),files={};
function add(source,name){if(fs.statSync(source).isDirectory()){for(const child of fs.readdirSync(source))add(path.join(source,child),name+'/'+child);}else files[name]=fs.readFileSync(source).toString('base64');}
add(path.join(root,'relay/server.mjs'),'server.mjs');add(path.join(root,'relay/package.json'),'package.json');add(path.join(root,'relay/web'),'web');add(fs.realpathSync(path.join(root,'relay/node_modules/ws')),'node_modules/ws');
const payload=zlib.gzipSync(Buffer.from(JSON.stringify(files)),{level:9}),hash=crypto.createHash('sha256').update(payload).digest('hex');
fs.mkdirSync(destination,{recursive:true});fs.writeFileSync(path.join(destination,'source.b64'),payload.toString('base64'));
const bootstrap=`const fs=require('node:fs'),path=require('node:path'),zlib=require('node:zlib'),crypto=require('node:crypto');
const payload=Buffer.from(fs.readFileSync('/opt/voicebridge/source.b64','utf8').trim(),'base64');
if(crypto.createHash('sha256').update(payload).digest('hex')!==${JSON.stringify(hash)})throw new Error('Deployment source integrity failed');
const files=JSON.parse(zlib.gunzipSync(payload,{maxOutputLength:5000000}));
const root='/tmp/voicebridge';fs.mkdirSync(root,{recursive:true});
for(const [name,encoded] of Object.entries(files)){const target=path.resolve(root,name);if(!target.startsWith(root+path.sep))throw new Error('Unsafe source path');fs.mkdirSync(path.dirname(target),{recursive:true});fs.writeFileSync(target,Buffer.from(encoded,'base64'));}
process.chdir(root);
import(require('node:url').pathToFileURL(path.join(root,'server.mjs')).href).then(async({startServer})=>{const running=await startServer();console.log('VoiceBridge relay ready: '+running.url);for(const signal of ['SIGTERM','SIGINT'])process.once(signal,()=>void running.stop().then(()=>process.exit()));}).catch(error=>{console.error(error.message);process.exitCode=1;});
`;
fs.writeFileSync(path.join(destination,'bootstrap-v3.cjs'),bootstrap);console.log(JSON.stringify({files:Object.keys(files).length,compressedBytes:payload.length,sha256:hash}));
