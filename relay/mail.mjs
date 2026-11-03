import tls from 'node:tls';
// Explicit opt-in and finite local quota are both required before production sends mail.
export function configuredMailer(env=process.env){
 if(env.MAIL_ENABLED!=='1'||!env.MAIL_USER||!env.MAIL_PASSWORD||!env.MAIL_FROM)return null;
 if(!/^[a-z0-9._+-]+@[a-z0-9.-]+$/i.test(env.MAIL_FROM)||/[\r\n]/.test(env.MAIL_USER))throw Error('Invalid mail sender configuration');
 return async({address,code,purpose,verifyOnly=false})=>{
  const socket=tls.connect({host:'smtpdm.aliyun.com',port:465,servername:'smtpdm.aliyun.com',minVersion:'TLSv1.2'});socket.setTimeout(15000,()=>socket.destroy(Error('Mail timeout')));
  let pending='',lines=[],waiting=[],failure;
  socket.on('data',bytes=>{pending+=bytes.toString('utf8');if(pending.length>65536){socket.destroy(Error('Mail response too large'));return;}let end;while((end=pending.indexOf('\r\n'))>=0){const line=pending.slice(0,end);pending=pending.slice(end+2);if(/^\d{3} /.test(line)){if(waiting.length)waiting.shift().resolve(Number(line.slice(0,3)));else lines.push(Number(line.slice(0,3)));}}});
  socket.on('error',()=>{failure=Error('邮件发送暂时不可用');for(const item of waiting.splice(0))item.reject(failure);});socket.on('close',()=>{failure??=Error('邮件连接已关闭');for(const item of waiting.splice(0))item.reject(failure);});
  const response=()=>failure?Promise.reject(failure):lines.length?Promise.resolve(lines.shift()):new Promise((resolve,reject)=>waiting.push({resolve,reject}));
  const expect=async(allowed)=>{const code=await response();if(!allowed.includes(code))throw Error('邮件服务未接受发送请求');};
  const command=async(value,allowed)=>{socket.write(value+'\r\n');await expect(allowed);};
  try{await expect([220]);await command('EHLO voicebridge-room.cn',[250]);await command('AUTH LOGIN',[334]);await command(Buffer.from(env.MAIL_USER).toString('base64'),[334]);await command(Buffer.from(env.MAIL_PASSWORD).toString('base64'),[235]);if(verifyOnly){socket.write('QUIT\r\n');return;}await command('MAIL FROM:<'+env.MAIL_FROM+'>',[250]);await command('RCPT TO:<'+address+'>',[250,251]);await command('DATA',[354]);const subject='VoiceBridge '+(purpose==='reset'?'重设密码':'注册')+'验证码';const content='你的验证码为：'+code+'\n\n五分钟内有效。请勿将验证码告诉他人。如果不是你本人操作，请忽略此邮件。';const message=['From: VoiceBridge <'+env.MAIL_FROM+'>','To: <'+address+'>','Subject: =?UTF-8?B?'+Buffer.from(subject).toString('base64')+'?=','MIME-Version: 1.0','Content-Type: text/plain; charset=UTF-8','Content-Transfer-Encoding: base64','',''+Buffer.from(content).toString('base64').match(/.{1,76}/g).join('\r\n')].join('\r\n');await command(message+'\r\n.',[250]);socket.write('QUIT\r\n');}finally{socket.end();socket.destroy();}
 };
}
