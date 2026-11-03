import {parentPort,workerData} from 'node:worker_threads';import WebSocket from 'ws';
globalThis.self=globalThis;globalThis.location=new URL(workerData.origin);const originalFetch=globalThis.fetch;globalThis.fetch=(url,options)=>originalFetch(new URL(url,workerData.origin),options);
globalThis.WebSocket=class extends WebSocket{constructor(url){super(url,{origin:workerData.origin});}};
self.postMessage=value=>parentPort.postMessage(value);
// A deterministic codec double proves transport/gating, not real Opus sound quality.
globalThis.AudioData=class{constructor(value){Object.assign(this,value);}close(){}};
globalThis.AudioEncoder=class{
 constructor({output,error}){this.output=output;this.error=error;this.state='unconfigured';this.encodeQueueSize=0;}
 configure(){this.state='configured';}
 encode(frame){queueMicrotask(()=>{if(this.state==='closed')return;this.output({timestamp:frame.timestamp,byteLength:8,copyTo:bytes=>bytes.set([1,2,3,4,5,6,7,8])});});}
 close(){this.state='closed';}
};
parentPort.on('message',value=>self.onmessage({data:value}));
await import('../web/room-runtime.worker.js');parentPort.postMessage({type:'hostReady'});
