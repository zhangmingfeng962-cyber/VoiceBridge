// Opt-in synthetic-demo verification only. Reads the private demo report; never prints invitation keys.
import {readFile,writeFile} from 'node:fs/promises';
import {webcrypto} from 'node:crypto';
import assert from 'node:assert/strict';
const [reportPath,outputPath]=process.argv.slice(2);
if(!reportPath)throw new Error('Usage: node public-probe.mjs PRIVATE_DEMO_REPORT [SANITIZED_RESULT]');
const report=JSON.parse(await readFile(reportPath,'utf8'));
assert.equal(report.AudioCaptureOpened,false);assert.equal(report.MicrophoneOpened,false);
const invitation=new URL(report.PublicLink),room=invitation.pathname.split('/').at(-1),[token,keyHex]=invitation.hash.slice(1).split('.');
const key=await webcrypto.subtle.importKey('raw',Buffer.from(keyHex,'hex'),{name:'AES-GCM'},false,['decrypt']);
let after=-1,checked=0,maxError=0,sequenceGaps=0,previousPacket=null;
const timings=[];const deadline=Date.now()+20000;
while(checked<3&&Date.now()<deadline){
  const begin=performance.now();
  const response=await fetch(new URL(`/api/rooms/${room}/next?after=${after}`,invitation.origin),{headers:{Authorization:'Bearer '+token},signal:AbortSignal.timeout(8000)});
  if(response.status===204){await new Promise(resolve=>setTimeout(resolve,150));continue;}
  assert.equal(response.status,200);
  const encrypted=await response.arrayBuffer(),header=new DataView(encrypted),sequence=header.getUint32(4,true);
  assert.equal(header.getUint32(0,true),0x31454256);assert.equal(encrypted.byteLength,154276);
  const plain=await webcrypto.subtle.decrypt({name:'AES-GCM',iv:new Uint8Array(encrypted,8,12),additionalData:new TextEncoder().encode(room+'|'+sequence),tagLength:128},key,encrypted.slice(20));
  assert.equal(plain.byteLength,154240);const pcm=new DataView(plain);
  for(let packet=0;packet<40;packet++){
    const offset=packet*3856,packetSequence=pcm.getUint32(offset+4,true);
    assert.equal(pcm.getUint32(offset,true),0x314d4256);assert.equal(pcm.getUint32(offset+8,true),48000);assert.equal(pcm.getUint16(offset+12,true),2);
    if(previousPacket!==null&&packetSequence!==((previousPacket+1)>>>0))sequenceGaps++;previousPacket=packetSequence;
    for(let sample=0;sample<480;sample++)for(let channel=0;channel<2;channel++){
      const frame=packetSequence*480+sample,frequency=channel===0?437:997;
      const expected=Math.fround(Math.fround(.02)*Math.fround(Math.sin(Math.fround(2*Math.PI*frequency*frame/48000))));
      const value=pcm.getFloat32(offset+16+sample*8+channel*4,true);
      assert.ok(Number.isFinite(value));maxError=Math.max(maxError,Math.abs(value-expected));
    }
  }
  after=sequence;checked++;timings.push(performance.now()-begin);
}
assert.equal(checked,3);assert.ok(maxError<.000002,'Received PCM differs from the known synthetic source');
const result={RelayOrigin:invitation.origin,EncryptedChunksVerified:checked,StereoSamplesVerified:checked*40*960,MaximumSyntheticSampleError:maxError,SourcePacketSequenceGaps:sequenceGaps,MeanFetchAndVerificationMs:timings.reduce((a,b)=>a+b,0)/timings.length,MicrophoneOpened:false,RealMusicRecorded:false,Note:'Synthetic upload and download via a public relay; not a friend-network or listening test.'};
if(outputPath)await writeFile(outputPath,JSON.stringify(result,null,2));console.log(JSON.stringify(result,null,2));
