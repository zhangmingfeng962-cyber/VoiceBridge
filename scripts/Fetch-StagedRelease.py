"""Fetch a pinned public CI bundle; credentials stay in the GitHub runner."""
import base64,concurrent.futures,gzip,hashlib,io,json,os,re,sys,tarfile,urllib.request,time
from pathlib import Path
commit,expected,out=sys.argv[1:4];token=os.environ['GH_TOKEN'];repo=os.environ['GITHUB_REPOSITORY'];dest=Path(out);dest.mkdir(parents=True,exist_ok=True)
if repo!='zhangmingfeng962-cyber/VoiceBridge' or not re.fullmatch('[a-f0-9]{40}',commit) or not re.fullmatch('[a-f0-9]{64}',expected):raise SystemExit('Pinned public release required')
def fetch(route,maxbytes,accept='application/vnd.github+json'):
 for attempt in range(5):
  try:
   request=urllib.request.Request('https://api.github.com/repos/'+repo+'/'+route,headers={'Authorization':'Bearer '+token,'Accept':accept,'User-Agent':'VoiceBridge-publisher'})
   with urllib.request.urlopen(request,timeout=120) as response:data=response.read(maxbytes+1)
   if len(data)>maxbytes:raise ValueError('Response size bound')
   return data
  except (OSError,TimeoutError):
   if attempt==4:raise
   time.sleep(2**attempt)
api=json.loads(fetch('contents/deployment/release-transfer-0.12.0.json?ref='+commit,131072));raw=base64.b64decode(api['content']);
if hashlib.sha256(raw).hexdigest()!=expected:raise SystemExit('Manifest SHA256 mismatch')
meta=json.loads(raw);allowed={'VoiceBridgeSetup.exe','VoiceBridge.Desktop-0.12.0-win-x64-full.nupkg','releases.win-x64.json','VoiceBridge-v0.12.0-source.zip'}
if meta['version']!='0.12.0' or set(meta['files'])!=allowed:raise SystemExit('Unexpected release files')
def piece(c):
 if not re.fullmatch('[a-f0-9]{40}',c['blob']) or not 0<c['bytes']<=4194304:raise ValueError('Chunk invalid')
 data=fetch('git/blobs/'+c['blob'],c['bytes']*2+8192,'application/vnd.github.raw+json')
 if len(data)!=c['bytes']:data=base64.b64decode(json.loads(data)['content'])
 if len(data)!=c['bytes'] or hashlib.sha256(data).hexdigest()!=c['sha256']:raise ValueError('Chunk SHA256 mismatch')
 return data
with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:packed=b''.join(pool.map(piece,meta['chunks']))
if len(packed)!=meta['bytes'] or hashlib.sha256(packed).hexdigest()!=meta['sha256']:raise SystemExit('Archive integrity mismatch')
with tarfile.open(fileobj=io.BytesIO(packed),mode='r:gz') as tar:
 seen=set()
 for item in tar:
  if item.name not in allowed or item.name in seen or not item.isfile() or item.size!=meta['files'][item.name]['bytes'] or item.size>100000000:raise ValueError('Unsafe release archive')
  data=tar.extractfile(item).read()
  if hashlib.sha256(data).hexdigest()!=meta['files'][item.name]['sha256']:raise ValueError('File SHA256 mismatch')
  (dest/item.name).write_bytes(data);seen.add(item.name)
 if seen!=allowed:raise ValueError('Release archive incomplete')
(dest/'SHA256SUMS.txt').write_text(''.join(value['sha256']+'  '+name+'\n' for name,value in meta['files'].items()),encoding='ascii')
print(json.dumps({'sourceCommit':meta['sourceCommit'],'verified':list(meta['files'])}))
