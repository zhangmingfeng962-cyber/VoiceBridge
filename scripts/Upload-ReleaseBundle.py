"""Stage only public, validated release files. No deployment credentials or signing key."""
import base64,concurrent.futures,gzip,hashlib,io,json,os,tarfile,time,urllib.request,urllib.error
from pathlib import Path
repo=os.environ['GITHUB_REPOSITORY'];token=os.environ['GH_TOKEN'];commit=os.environ['GITHUB_SHA'];branch='voicebridge-0.13.0'
if repo!='zhangmingfeng962-cyber/VoiceBridge' or os.environ['GITHUB_REF']!='refs/heads/'+branch:raise SystemExit('Candidate branch required')
def api(method,route,body=None):
 for attempt in range(5):
  request=urllib.request.Request('https://api.github.com/repos/'+repo+'/'+route,data=json.dumps(body).encode() if body is not None else None,method=method,headers={'Authorization':'Bearer '+token,'Accept':'application/vnd.github+json','Content-Type':'application/json','User-Agent':'VoiceBridge-release'})
  try:
   with urllib.request.urlopen(request,timeout=120) as response:return json.load(response)
  except (urllib.error.URLError,TimeoutError):
   if attempt==4:raise
   time.sleep(2**attempt)
files={
 'VoiceBridgeSetup.exe':Path('artifacts/installer/VoiceBridge.Desktop-win-x64-Setup.exe'),
 'VoiceBridge.Desktop-0.13.0-win-x64-full.nupkg':Path('artifacts/installer/VoiceBridge.Desktop-0.13.0-win-x64-full.nupkg'),
 'releases.win-x64.json':Path('artifacts/installer/releases.win-x64.json'),
 'VoiceBridge-v0.13.0-source.zip':Path('artifacts/source/VoiceBridge-v0.13.0-source.zip')}
delta=Path('artifacts/installer/VoiceBridge.Desktop-0.13.0-win-x64-delta.nupkg')
if delta.exists():files[delta.name]=delta
metadata={name:{'bytes':file.stat().st_size,'sha256':hashlib.sha256(file.read_bytes()).hexdigest()} for name,file in files.items()}
archive=Path('artifacts/public-release.tar.gz')
with archive.open('wb') as output:
 with gzip.GzipFile(fileobj=output,mode='wb',mtime=0) as compressed:
  with tarfile.open(fileobj=compressed,mode='w|',format=tarfile.USTAR_FORMAT) as tar:
   for name,file in files.items():
    info=tarfile.TarInfo(name);info.size=file.stat().st_size;info.mode=0o644;info.mtime=0
    with file.open('rb') as source:tar.addfile(info,source)
packed=archive.read_bytes();pieces=[packed[n:n+4194304] for n in range(0,len(packed),4194304)]
def upload(piece):
 blob=api('POST','git/blobs',{'encoding':'base64','content':base64.b64encode(piece).decode()})
 return {'blob':blob['sha'],'bytes':len(piece),'sha256':hashlib.sha256(piece).hexdigest()}
with concurrent.futures.ThreadPoolExecutor(max_workers=3) as workers:chunks=list(workers.map(upload,pieces))
relay=base64.b64decode(Path('artifacts/offline-relay/source.b64').read_text());relay_blob=api('POST','git/blobs',{'encoding':'base64','content':base64.b64encode(relay).decode()})['sha']
manifest={'relay':{'blob':relay_blob,'bytes':len(relay),'sha256':hashlib.sha256(relay).hexdigest()},'version':'0.13.0','sourceCommit':commit,'encoding':'tar-gzip-chunks-v1','bytes':len(packed),'sha256':hashlib.sha256(packed).hexdigest(),'files':metadata,'feedBase64':base64.b64encode(files['releases.win-x64.json'].read_bytes()).decode(),'chunks':chunks}
head=api('GET','git/ref/heads/'+branch)['object']['sha']
if head!=commit:raise SystemExit('Candidate changed during build; stage the new build instead')
entries=[{'path':'deployment/release-transfer-0.13.0.json','mode':'100644','type':'blob','content':json.dumps(manifest,indent=2)}, {'path':'deployment/relay-0.13.0-source.gz','mode':'100644','type':'blob','sha':relay_blob}]
tree=api('POST','git/trees',{'base_tree':commit,'tree':entries})
created=api('POST','git/commits',{'message':'Stage checked public 0.13.0 artifacts [skip ci]','tree':tree['sha'],'parents':[commit]})
api('PATCH','git/refs/heads/'+branch,{'sha':created['sha'],'force':False})
print(json.dumps({'transferCommit':created['sha'],'sourceCommit':commit,'chunks':len(chunks),'bytes':len(packed),'sha256':manifest['sha256']}))
