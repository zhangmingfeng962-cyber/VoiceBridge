const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
function create() {
  let Processor;
  const sandbox = {AudioWorkletProcessor: class {constructor() {this.port = {postMessage() {}};}}, registerProcessor(name, ctor) {Processor = ctor;}, sampleRate:48000, ArrayBuffer, DataView, Float32Array, Number, Math};
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../../src/VoiceBridge.App/Sharing/Web/pcm-worklet.js'), 'utf8'), sandbox);
  return new Processor({processorOptions:{bufferMs:120}});
}
function packet(sequence, l = .2, r = -.3) {
  const data = new ArrayBuffer(3856), view = new DataView(data);
  view.setUint32(0, 0x314d4256, true); view.setUint32(4, sequence, true); view.setUint32(8, 48000, true);
  view.setUint16(12, 2, true); view.setUint16(14, 480, true);
  for(let i=0;i<480;i++){view.setFloat32(16+i*8,l,true);view.setFloat32(20+i*8,r,true);}
  return data;
}
function render(p) {const out = [[new Float32Array(128), new Float32Array(128)]]; p.process([],out); return out[0];}
test('waits for chosen buffer and preserves separate stereo levels without AGC', () => {
  const p=create(); p.packet(packet(0)); assert.ok(render(p)[0].every(x=>x===0));
  for(let i=1;i<12;i++)p.packet(packet(i));
  render(p); render(p); const [l,r]=render(p);
  assert.ok(l.every(x=>Math.abs(x-.2)<1e-6));assert.ok(r.every(x=>Math.abs(x+.3)<1e-6));
});
test('underflow re-buffers and silence stays silent', () => {
  const p=create(); for(let i=0;i<12;i++)p.packet(packet(i,0,0));
  for(let i=0;i<70;i++) assert.ok(render(p)[0].every(x=>x===0));
  assert.equal(p.underruns,1); assert.equal(p.playing,false);
  for(let i=12;i<24;i++)p.packet(packet(i,0,0)); render(p);assert.equal(p.playing,true);
});
test('sequence jumps discard stale audio and wrap at uint32 correctly', () => {
  const p=create();p.packet(packet(0xffffffff));p.packet(packet(0));assert.equal(p.gaps,0);
  p.packet(packet(3));assert.equal(p.gaps,1);assert.equal(p.size,480);
});
test('invalid packets cannot alter the queue and a long stale burst stays bounded', () => {
  const p=create();p.packet(new ArrayBuffer(5000));assert.equal(p.size,0);
  const bad=packet(0);new DataView(bad).setUint32(8,44100,true);p.packet(bad);assert.equal(p.size,0);
  for(let i=0;i<500;i++)p.packet(packet(i));
  assert.ok(p.resets>0);assert.ok(p.size<=p.target+24000);
});
test('invalid samples are silenced, peaks bounded, and receiver reset flushes old audio', () => {
  const p=create();for(let i=0;i<12;i++)p.packet(packet(i,Infinity,2));
  for(let i=0;i<5;i++){const [l,r]=render(p);assert.ok(l.every(x=>x===0));assert.ok(r.every(x=>Number.isFinite(x)&&Math.abs(x)<=1));}
  p.port.onmessage({data:{type:'reset'}});assert.equal(p.size,0);assert.equal(p.sequence,null);assert.ok(render(p)[1].every(x=>x===0));
});
