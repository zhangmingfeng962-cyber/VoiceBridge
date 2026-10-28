import test from 'node:test';
import assert from 'node:assert/strict';
import {ScreenShare} from '../web/screen.js';
const session='a'.repeat(32);
function video(surface='window'){
  return {kind:'video',enabled:true,readyState:'live',stops:0,getSettings:()=>({displaySurface:surface,height:720}),applyConstraints:async()=>{},stop(){this.stops++;this.readyState='ended';}};
}
function stream(track=video(),audio=[]){return {getTracks:()=>[track,...audio],getVideoTracks:()=>[track],getAudioTracks:()=>audio,removeTrack:item=>audio.splice(audio.indexOf(item),1)};}
function engine(capture){
  const sent=[],states=[],videos=[];let screen;
  screen=new ScreenShare({selfId:'self',capture,send:m=>sent.push(m),onState:m=>states.push(m),onVideo:(s,local)=>videos.push([s,local]),createPeer:()=>{throw Error('No unexpected peer should be created');},makeStream:track=>stream(track)});
  screen.configure({version:1,iceServers:[],expires:1,relayAvailable:false});return {screen,sent,states,videos};
}
async function begin(ctx){
  const work=ctx.screen.select();await new Promise(resolve=>setImmediate(resolve));
  const request=ctx.sent.find(m=>m.type==='screen:claim');assert.ok(request);
  await ctx.screen.receive({type:'screen:claimed',requestId:request.requestId,session});
  await ctx.screen.receive({type:'screen:state',owner:'self',session,viewers:[],maxBitrate:1400000,paused:false,mode:'balanced',surface:'window'});await work;
}

test('stopping while the OS picker is open releases a late stream without publishing it',async()=>{
  let finish;const ctx=engine(()=>new Promise(resolve=>finish=resolve)),track=video(),work=ctx.screen.select();ctx.screen.stop();finish(stream(track));await work;
  assert.ok(track.stops>0);assert.equal(ctx.sent.some(m=>m.type==='screen:claim'),false);assert.equal(ctx.screen.stream,null);ctx.screen.close();
});
test('denied source switch retains the existing capture and peer tracks',async()=>{
  const original=video();let options;const ctx=engine(async value=>{options=value;return stream(original);});try{
    await begin(ctx);assert.equal(options.audio,false);assert.equal(options.systemAudio,'exclude');
    ctx.screen.capture=async()=>{throw Object.assign(Error('Denied'),{name:'NotAllowedError'});};await ctx.screen.select(true);
    assert.equal(ctx.screen.stream.getVideoTracks()[0],original);assert.equal(original.stops,0);assert.equal(ctx.screen.selecting,false);
  }finally{ctx.screen.close();}
});
test('a late initial room announcement preserves the presenters local preview',async()=>{
  const track=video(),ctx=engine(async()=>stream(track));try{
    const work=ctx.screen.select();await new Promise(resolve=>setImmediate(resolve));const request=ctx.sent.at(-1);
    await ctx.screen.receive({type:'screen:claimed',requestId:request.requestId,session});await work;
    await ctx.screen.receive({type:'screen:state',owner:'self',session,viewers:[],maxBitrate:1400000,paused:false,mode:'balanced',surface:'window'});
    assert.equal(ctx.videos.at(-1)[0],ctx.screen.stream);assert.equal(ctx.videos.at(-1)[1],true);
  }finally{ctx.screen.close();}
});
test('failed replacement rolls back peers already switched before stopping the new track',async()=>{
  const original=video(),next=video('monitor'),ctx=engine(async()=>stream(original));try{
    await begin(ctx);const replaced=[];ctx.screen.links.set('a',{peer:'a',sender:{replaceTrack:async track=>replaced.push(track)},pc:{close(){}},work:Promise.resolve()});
    ctx.screen.links.set('b',{peer:'b',sender:{replaceTrack:async()=>{throw Error('Cannot replace');}},pc:{close(){}},work:Promise.resolve()});
    ctx.screen.capture=async()=>stream(next);await ctx.screen.select(true);
    assert.deepEqual(replaced,[next,original]);assert.equal(ctx.screen.stream.getVideoTracks()[0],original);assert.ok(next.stops>0);assert.equal(original.stops,0);
  }finally{ctx.screen.close();}
});
test('pause sends no live video track and resumes the same capture without touching audio',async()=>{
  const track=video(),audio={kind:'audio',stops:0,stop(){this.stops++;}},ctx=engine(async()=>stream(track,[audio]));try{
    await begin(ctx);assert.equal(audio.stops,1);assert.equal(ctx.screen.stream.getAudioTracks().length,0);
    const replacements=[];ctx.screen.links.set('viewer',{peer:'viewer',sender:{replaceTrack:async value=>replacements.push(value)},pc:{close(){}},work:Promise.resolve()});
    await ctx.screen.pause(true);assert.equal(track.enabled,false);assert.equal(replacements.at(-1),null);assert.equal(ctx.sent.at(-1).paused,true);
    await ctx.screen.pause(false);assert.equal(track.enabled,true);assert.equal(replacements.at(-1),track);assert.equal(ctx.sent.at(-1).paused,false);
  }finally{ctx.screen.close();}
});
test('a cancelled claim cannot leave a late server reservation or capture alive',async()=>{
  const track=video(),ctx=engine(async()=>stream(track));const work=ctx.screen.select();await new Promise(resolve=>setImmediate(resolve));const request=ctx.sent.at(-1);ctx.screen.stop();await work;
  await ctx.screen.receive({type:'screen:claimed',requestId:request.requestId,session});assert.equal(ctx.sent.at(-1).type,'screen:stop');assert.equal(ctx.sent.at(-1).session,session);assert.ok(track.stops>0);ctx.screen.close();
});
test('a busy room rejection releases capture; ending an OS capture releases presentation',async()=>{
  const busyTrack=video(),busy=engine(async()=>stream(busyTrack)),work=busy.screen.select();await new Promise(resolve=>setImmediate(resolve));const request=busy.sent.at(-1);
  await busy.screen.receive({type:'screen:error',code:'busy',requestId:request.requestId});await work;assert.ok(busyTrack.stops>0);assert.equal(busy.screen.stream,null);busy.screen.close();
  const track=video(),ctx=engine(async()=>stream(track));await begin(ctx);track.onended();assert.equal(ctx.screen.stream,null);assert.equal(ctx.sent.at(-1).type,'screen:stop');ctx.screen.close();
});
test('bandwidth limits remain below the shared budget and congestion lowers video without changing capture ownership',async()=>{
  const ctx=engine(async()=>stream());try{
    await begin(ctx);ctx.screen.state.maxBitrate=180000;const parameters=[];
    const item={peer:'v',owner:true,sender:{getParameters:()=>({encodings:[{}]}),setParameters:async p=>parameters.push(p)},work:Promise.resolve(),factor:1,stable:0,pc:{getStats:async()=>new Map([['pair',{type:'candidate-pair',state:'succeeded',nominated:true,currentRoundTripTime:.5,availableOutgoingBitrate:100000}]])}};
    ctx.screen.links.set('v',item);await ctx.screen.limit(item);assert.ok(parameters.at(-1).encodings[0].maxBitrate<=180000);assert.ok(parameters.at(-1).encodings[0].maxFramerate<=15);
    await ctx.screen.statistics(item);assert.ok(parameters.at(-1).encodings[0].maxBitrate<180000);assert.equal(ctx.screen.session,session);
    item.pc.close=()=>{};
  }finally{ctx.screen.close();}
});
test('an application-limited static screen does not trigger repeated false network downscaling',async()=>{
  const ctx=engine(async()=>stream());try{
    await begin(ctx);const item={peer:'v',owner:true,sender:{getParameters:()=>({encodings:[{}]}),setParameters:async()=>{}},work:Promise.resolve(),factor:1,stable:0,pc:{close(){},getStats:async()=>new Map([['p',{type:'candidate-pair',state:'succeeded',nominated:true,currentRoundTripTime:.01,availableOutgoingBitrate:100000}],['v',{type:'outbound-rtp',kind:'video',qualityLimitationReason:'none'}]])}};
    ctx.screen.links.set('v',item);for(let n=0;n<4;n++)await ctx.screen.statistics(item);assert.equal(item.factor,1);assert.equal(item.bitrate,1400000);
  }finally{ctx.screen.close();}
});
