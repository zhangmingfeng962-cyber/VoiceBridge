import fs from 'node:fs/promises';import {configuredMailer} from '../relay/mail.mjs';
try{
 const text=await fs.readFile(process.argv[2]||'/etc/voicebridge/relay.env','utf8'),settings={};
 for(const line of text.split(/\r?\n/)){const pair=/^(MAIL_[A-Z_]+)=(.*)$/.exec(line);if(pair)settings[pair[1]]=pair[2].startsWith('"')?JSON.parse(pair[2]):pair[2];}
 const verify=configuredMailer(settings);if(!verify)throw Error();await verify({verifyOnly:true});console.log('SMTP_AUTHENTICATION_VERIFIED_NO_MESSAGE_SENT');
}catch{console.error('SMTP_AUTHENTICATION_CHECK_FAILED');process.exitCode=1;}
