import {randomInt} from 'node:crypto';
import {startServer as start} from '../server.mjs';
// Windows can allocate port 0 from a range containing Fetch-blocked ports (for
// example 10080). Keep test listeners in the unrestricted high range instead.
export async function startServer(options){
  if(options.port!==0)return start(options);
  for(let attempt=0;attempt<10;attempt++)try{return await start({...options,port:20000+randomInt(40000)});}catch(error){if(error.code!=='EADDRINUSE')throw error;}
  throw Error('Could not reserve a test server port');
}
