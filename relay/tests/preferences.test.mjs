import test from 'node:test';import assert from 'node:assert/strict';
import {readPreferences,savePreferences,cleanNickname} from '../web/preferences.js';
import {BrowserAudio} from '../web/audio.js';
test('local preferences remember nickname and suppression and survive malformed or unavailable storage',()=>{
 const data=new Map(),storage={getItem:key=>data.get(key),setItem:(key,value)=>data.set(key,value)};
 assert.deepEqual(readPreferences(storage),{nickname:'朋友',noiseSuppression:true});assert.ok(savePreferences({nickname:'  新昵称\u0000 ',noiseSuppression:false},storage));assert.deepEqual(readPreferences(storage),{nickname:'新昵称',noiseSuppression:false});assert.equal(cleanNickname('a'.repeat(25)).length,20);
 data.set('voicebridge.preferences.v1','{broken');assert.equal(readPreferences(storage).noiseSuppression,true);const blocked={getItem:()=>{throw Error('Blocked');},setItem:()=>{throw Error('Blocked');}};assert.equal(savePreferences({nickname:'朋友'},blocked),false);
});
test('microphone noise toggle applies only to microphone and never alters the music capture',async()=>{
 const audio=new BrowserAudio(()=>{},()=>{},()=>{}),calls=[],music={identity:1};let noise=true;
 audio.capture[0]={stream:{getAudioTracks:()=>[{applyConstraints:async value=>{calls.push(value);noise=value.noiseSuppression;},getSettings:()=>({noiseSuppression:noise})}]}};audio.capture[1]=music;
 assert.deepEqual(await audio.noiseSuppression(false),{requested:false,actual:false});assert.equal(audio.capture[1],music);assert.deepEqual(calls,[{noiseSuppression:false,autoGainControl:false}]);
});
