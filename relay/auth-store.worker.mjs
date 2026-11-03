import {parentPort,workerData} from 'node:worker_threads';
import {DatabaseSync} from 'node:sqlite';
import {randomBytes,randomInt,createHash,createHmac,scryptSync,timingSafeEqual} from 'node:crypto';
import {mkdirSync,readFileSync,writeFileSync,existsSync} from 'node:fs';
import path from 'node:path';
import {PNG} from 'pngjs';

const {directory,freeRemaining=0,dailyLimit=180}=workerData;
mkdirSync(directory,{recursive:true,mode:0o700});mkdirSync(path.join(directory,'avatars'),{recursive:true,mode:0o700});
const secretPath=path.join(directory,'challenge.key');
if(!existsSync(secretPath))writeFileSync(secretPath,randomBytes(32),{mode:0o600,flag:'wx'});
const secret=readFileSync(secretPath),db=new DatabaseSync(path.join(directory,'accounts.sqlite'));
db.exec(`PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;
 CREATE TABLE IF NOT EXISTS users(id TEXT PRIMARY KEY,email TEXT NOT NULL UNIQUE,password TEXT NOT NULL,nickname TEXT NOT NULL,avatar TEXT NOT NULL DEFAULT '',created INTEGER NOT NULL);
 CREATE TABLE IF NOT EXISTS sessions(hash TEXT PRIMARY KEY,user_id TEXT NOT NULL REFERENCES users(id),expires INTEGER NOT NULL);
 CREATE TABLE IF NOT EXISTS challenges(email TEXT NOT NULL,purpose TEXT NOT NULL,digest TEXT NOT NULL,expires INTEGER NOT NULL,attempts INTEGER NOT NULL DEFAULT 0,issued INTEGER NOT NULL,PRIMARY KEY(email,purpose));
 CREATE TABLE IF NOT EXISTS verified(hash TEXT PRIMARY KEY,email TEXT NOT NULL,purpose TEXT NOT NULL,expires INTEGER NOT NULL);
 CREATE TABLE IF NOT EXISTS limits(key TEXT PRIMARY KEY,until INTEGER NOT NULL,count INTEGER NOT NULL);
 CREATE TABLE IF NOT EXISTS budget(id INTEGER PRIMARY KEY CHECK(id=1),remaining INTEGER NOT NULL);
 CREATE TABLE IF NOT EXISTS daily(day TEXT PRIMARY KEY,count INTEGER NOT NULL);
 CREATE INDEX IF NOT EXISTS sessions_user ON sessions(user_id);`);
db.prepare('INSERT OR IGNORE INTO budget(id,remaining) VALUES(1,?)').run(Math.max(0,Math.trunc(freeRemaining)));
const now=()=>Date.now(),hex=n=>randomBytes(n).toString('hex'),sha=v=>createHash('sha256').update(v).digest('hex');
const fail=(status,message)=>{throw Object.assign(Error(message),{status});};
const equal=(a,b)=>typeof a==='string'&&typeof b==='string'&&a.length===b.length&&timingSafeEqual(Buffer.from(a),Buffer.from(b));
const email=value=>{const s=typeof value==='string'?value.trim().toLowerCase():'';if(s.length>254||!/^[-.!#$%&'*+/=?^_`{|}~a-z0-9]+@[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?\.[a-z]{2,63}$/.test(s))fail(400,'请输入有效的邮箱地址');return s;};
const nickname=v=>{const name=typeof v==='string'?v.replace(/[\p{C}]/gu,'').trim().slice(0,20):'';if(!name)fail(400,'请填写昵称');return name;};
const publicProfile=u=>({id:u.id,nickname:u.nickname,avatarUrl:u.avatar?'/avatars/'+u.avatar+'.png':null,shortId:u.id.slice(0,6)});
function limit(key,count,milliseconds){const row=db.prepare('SELECT * FROM limits WHERE key=?').get(key);if(row&&row.until>now()&&row.count>=count)fail(429,'操作较频繁，请稍后再试');db.prepare('INSERT OR REPLACE INTO limits VALUES(?,?,?)').run(key,row?.until>now()?row.until:now()+milliseconds,row?.until>now()?row.count+1:1);}
function password(value){if(typeof value!=='string')fail(400,'请填写密码');const s=value.normalize('NFC');if([...s].length<15||[...s].length>128||Buffer.byteLength(s)>1024)fail(400,'密码需为 15–128 个字符，可以使用中文、空格或一段短语');return s;}
function digest(value,salt){return scryptSync(value,salt,64,{N:131072,r:8,p:1,maxmem:192*1024*1024}).toString('hex');}
function hashPassword(value){const salt=hex(16);return 'scrypt$131072$8$1$'+salt+'$'+digest(password(value),salt);}
const dummySalt=hex(16),dummyHash=digest('VoiceBridge timing comparison',dummySalt);
function checkPassword(value,stored){const parts=(stored||'').split('$');const salt=parts.length===6?parts[4]:dummySalt;const valid=typeof value==='string'&&Buffer.byteLength(value)<=1024;const actual=digest(valid?value.normalize('NFC'):'',salt);return valid&&parts.length===6&&equal(actual,parts[5]||dummyHash);}
function createSession(user){const token=hex(32),expires=now()+30*86400000;db.prepare('INSERT INTO sessions VALUES(?,?,?)').run(sha(token),user.id,expires);return {token,expires,user:publicProfile(user)};}
function who(token){if(typeof token!=='string'||!/^[a-f0-9]{64}$/.test(token))return null;const row=db.prepare('SELECT users.*,sessions.hash AS sessionHash,sessions.expires FROM sessions JOIN users ON users.id=sessions.user_id WHERE sessions.hash=? AND sessions.expires>?').get(sha(token),now());return row?{user:publicProfile(row),userId:row.id,sessionHash:row.sessionHash,expires:row.expires}:null;}
function requireSession(token){const identity=who(token);if(!identity)fail(401,'登录已失效，请重新登录');return identity;}
function verification(ticket,purpose){if(typeof ticket!=='string')fail(400,'请先验证邮箱');const value=db.prepare('SELECT * FROM verified WHERE hash=? AND purpose=? AND expires>?').get(sha(ticket),purpose,now());if(!value)fail(400,'邮箱验证已过期，请重新获取验证码');return value;}
function challengeDigest(address,purpose,code){return createHmac('sha256',secret).update(address+'\0'+purpose+'\0'+code).digest('hex');}
const actions={
 health(){return {freeRemaining:db.prepare('SELECT remaining FROM budget WHERE id=1').get().remaining};},
 requestCode({address,purpose,ip}){
  address=email(address);if(!['register','reset'].includes(purpose))fail(400,'验证码用途无效');
  limit('mail-minute:'+purpose+':'+address,1,60000);limit('mail-hour:'+address,5,3600000);limit('mail-ip:'+ip,12,3600000);
  const day=new Date(now()+8*3600000).toISOString().slice(0,10),budget=db.prepare('SELECT remaining FROM budget WHERE id=1').get(),sent=db.prepare('SELECT count FROM daily WHERE day=?').get(day)?.count||0;
  if(budget.remaining<=0||sent>=Math.min(180,dailyLimit))fail(503,'今日或当前免费邮件额度不足，暂时不能发送验证码；已登录的用户仍可使用');
  const code=String(randomInt(0,1000000)).padStart(6,'0');
  db.exec('BEGIN IMMEDIATE');try{db.prepare('UPDATE budget SET remaining=remaining-1 WHERE id=1').run();db.prepare('INSERT INTO daily VALUES(?,1) ON CONFLICT(day) DO UPDATE SET count=count+1').run(day);db.prepare('INSERT OR REPLACE INTO challenges VALUES(?,?,?,?,0,?)').run(address,purpose,challengeDigest(address,purpose,code),now()+300000,now());db.exec('COMMIT');}catch(error){db.exec('ROLLBACK');throw error;}
  return {address,code,purpose};
 },
 cancelCode({address,purpose}){db.prepare('DELETE FROM challenges WHERE email=? AND purpose=?').run(address,purpose);return {};},
 verifyCode({address,purpose,code,ip}){
  address=email(address);limit('verify:'+ip,30,60000);const row=db.prepare('SELECT * FROM challenges WHERE email=? AND purpose=?').get(address,purpose);
  if(!row||row.expires<now()||row.attempts>=5)fail(400,'验证码无效或已过期，请重新获取');
  db.prepare('UPDATE challenges SET attempts=attempts+1 WHERE email=? AND purpose=?').run(address,purpose);
  if(typeof code!=='string'||!/^\d{6}$/.test(code)||!equal(row.digest,challengeDigest(address,purpose,code)))fail(400,'验证码错误，请检查后重试');
  const token=hex(32);db.prepare('DELETE FROM challenges WHERE email=? AND purpose=?').run(address,purpose);db.prepare('INSERT INTO verified VALUES(?,?,?,?)').run(sha(token),address,purpose,now()+600000);return {verificationToken:token};
 },
 register({verificationToken,password:value,nickname:name}){
  const grant=verification(verificationToken,'register');name=nickname(name);const hashed=hashPassword(value);if(db.prepare('SELECT id FROM users WHERE email=?').get(grant.email))fail(409,'该邮箱已注册，请使用密码登录或找回密码');
  const user={id:hex(16),email:grant.email,password:hashed,nickname:name,avatar:'',created:now()};db.exec('BEGIN IMMEDIATE');try{db.prepare('INSERT INTO users VALUES(?,?,?,?,?,?)').run(user.id,user.email,user.password,user.nickname,'',user.created);db.prepare('DELETE FROM verified WHERE hash=?').run(sha(verificationToken));const result=createSession(user);db.exec('COMMIT');return result;}catch(error){db.exec('ROLLBACK');throw error;}
 },
 login({address,password:value,ip}){address=email(address);limit('login-ip:'+ip,15,60000);limit('login-email:'+address,8,60000);const user=db.prepare('SELECT * FROM users WHERE email=?').get(address);if(!checkPassword(value,user?.password))fail(401,'邮箱或密码不正确');return createSession(user);},
 who({token}){return who(token);},
 refresh({token}){const identity=requireSession(token),expires=now()+30*86400000;db.prepare('UPDATE sessions SET expires=? WHERE hash=?').run(expires,identity.sessionHash);return {token,expires,user:identity.user};},
 logout({token}){const identity=who(token);db.prepare('DELETE FROM sessions WHERE hash=?').run(sha(token||''));return {sessionHash:identity?.sessionHash};},
 reset({verificationToken,password:value}){const grant=verification(verificationToken,'reset'),user=db.prepare('SELECT * FROM users WHERE email=?').get(grant.email);const hashed=hashPassword(value);if(!user)fail(400,'该邮箱未注册，请先注册');db.exec('BEGIN IMMEDIATE');try{db.prepare('UPDATE users SET password=? WHERE id=?').run(hashed,user.id);db.prepare('DELETE FROM sessions WHERE user_id=?').run(user.id);db.prepare('DELETE FROM verified WHERE hash=?').run(sha(verificationToken));db.exec('COMMIT');return {revokedUserId:user.id};}catch(error){db.exec('ROLLBACK');throw error;}},
 changePassword({token,currentPassword,newPassword}){const identity=requireSession(token),user=db.prepare('SELECT * FROM users WHERE id=?').get(identity.userId);limit('password:'+user.id,5,60000);if(!checkPassword(currentPassword,user.password))fail(401,'当前密码不正确');const hashed=hashPassword(newPassword);db.prepare('UPDATE users SET password=? WHERE id=?').run(hashed,user.id);db.prepare('DELETE FROM sessions WHERE user_id=? AND hash<>?').run(user.id,identity.sessionHash);return {user:identity.user,revokedUserId:user.id,keepSessionHash:identity.sessionHash};},
 profile({token,nickname:name}){const identity=requireSession(token);db.prepare('UPDATE users SET nickname=? WHERE id=?').run(nickname(name),identity.userId);return {user:publicProfile(db.prepare('SELECT * FROM users WHERE id=?').get(identity.userId))};},
 renameUser({userId,nickname:name}){db.prepare('UPDATE users SET nickname=? WHERE id=?').run(nickname(name),userId);return {user:publicProfile(db.prepare('SELECT * FROM users WHERE id=?').get(userId))};},
 avatar({token,png}){const identity=requireSession(token);if(typeof png!=='string'||png.length>1400000||!/^[A-Za-z0-9+/]*={0,2}$/.test(png))fail(400,'请选择小于 1MB 的头像图片');const bytes=Buffer.from(png,'base64');if(bytes.length<24||bytes.subarray(0,8).toString('hex')!=='89504e470d0a1a0a')fail(400,'头像需要 PNG 格式');const width=bytes.readUInt32BE(16),height=bytes.readUInt32BE(20);if(width<16||height<16||width>1024||height>1024)fail(400,'头像尺寸需要介于 16 和 1024 像素');let decoded;try{decoded=PNG.sync.read(bytes,{checkCRC:true});}catch{fail(400,'头像图片无法读取');}const output=new PNG({width:256,height:256}),scale=Math.min(width,height)/256,ox=(width-Math.min(width,height))/2,oy=(height-Math.min(width,height))/2;for(let y=0;y<256;y++)for(let x=0;x<256;x++){const s=(Math.min(height-1,Math.floor(oy+y*scale))*width+Math.min(width-1,Math.floor(ox+x*scale)))*4;decoded.data.copy(output.data,(y*256+x)*4,s,s+4);}const normalized=PNG.sync.write(output),name=sha(normalized);writeFileSync(path.join(directory,'avatars',name+'.png'),normalized,{mode:0o600});db.prepare('UPDATE users SET avatar=? WHERE id=?').run(name,identity.userId);return {user:publicProfile(db.prepare('SELECT * FROM users WHERE id=?').get(identity.userId))};},
 cleanup(){db.prepare('DELETE FROM sessions WHERE expires<?').run(now());db.prepare('DELETE FROM challenges WHERE expires<?').run(now());db.prepare('DELETE FROM verified WHERE expires<?').run(now());db.prepare('DELETE FROM limits WHERE until<?').run(now());return {};},
};
parentPort.on('message',({id,operation,args})=>{try{if(!Object.hasOwn(actions,operation))fail(400,'Unknown account operation');const result=actions[operation](args||{});parentPort.postMessage({id,result});}catch(error){parentPort.postMessage({id,error:{status:error.status||500,message:error.status?error.message:'账号服务暂时不可用'}});}});
parentPort.postMessage({ready:true});
