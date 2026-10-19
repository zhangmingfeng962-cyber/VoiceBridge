export function membershipCues(previous,next,seen){
  if(!seen)return [true];const before=new Set(previous.map(m=>m.id)),after=new Set(next.map(m=>m.id)),cues=[];
  if(next.some(m=>!before.has(m.id)))cues.push(true);if(previous.some(m=>!after.has(m.id)))cues.push(false);return cues;
}
