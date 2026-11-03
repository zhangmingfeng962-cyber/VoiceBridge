import test from 'node:test';import assert from 'node:assert/strict';import {readRecovery,saveRecovery} from '../web/room-recovery.js';
const account='a'.repeat(32),member='b'.repeat(32),resume={memberId:member,token:'c'.repeat(64)},entry={token:'d'.repeat(64),key:'e'.repeat(64)};
const memory=()=>{const values=new Map();return {getItem:k=>values.get(k),setItem:(k,v)=>values.set(k,v)};};
test('refresh recovery preserves mute and individual gains but never restores sharing or held PTT',()=>{
 const storage=memory();assert.equal(saveRecovery(storage,'room',account,resume,{microphone:false,microphoneGain:170,speakerGain:45,musicGain:65,talkMode:'ptt',talkKey:86,pressed:true,sharing:true,memberVoice:{[member]:35},memberMusic:{[member]:20}},entry,1000),true);
 const restored=readRecovery(storage,'room',account,1200);assert.deepEqual(restored.value,resume);assert.equal(restored.microphone,false);assert.equal(restored.options.speakerGain,45);assert.equal(restored.options.memberVoice[member],35);assert.equal(restored.options.memberMusic[member],20);assert.equal(restored.options.talkMode,'ptt');assert.equal('pressed' in restored.options,false);assert.equal('sharing' in restored.options,false);assert.deepEqual(restored.admission,entry);
});
test('recovery never crosses accounts, expires after 90 seconds, and ignores corrupt data',()=>{
 const storage=memory();saveRecovery(storage,'room',account,resume,{microphone:true},entry,1000);assert.equal(readRecovery(storage,'room','f'.repeat(32),1100),null);assert.equal(readRecovery(storage,'room',account,91000),null);storage.setItem('room','{invalid');assert.equal(readRecovery(storage,'room',account,1100),null);
});
