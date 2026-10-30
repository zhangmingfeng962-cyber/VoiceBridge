"""Publish checksummed public build chunks through the official GitHub Git API."""
import base64
import concurrent.futures
import gzip
import hashlib
import json
import os
from pathlib import Path
import re
import sys
import time
import urllib.error
import urllib.request

repo = os.environ.get('GITHUB_REPOSITORY', '')
token = os.environ.get('GH_TOKEN', '')
version, filename = sys.argv[1:3]
if repo != 'zhangmingfeng962-cyber/VoiceBridge' or not token or not re.fullmatch(r'\d+\.\d+\.\d+', version):
    raise SystemExit('Expected repository, CI credential and release version')

def request(method, path, payload=None, missing_ok=False):
    data = json.dumps(payload).encode() if payload is not None else None
    for attempt in range(5):
        req = urllib.request.Request('https://api.github.com/repos/' + repo + '/' + path, data=data, method=method,
            headers={'Authorization': 'Bearer ' + token, 'Accept': 'application/vnd.github+json', 'Content-Type': 'application/json', 'X-GitHub-Api-Version': '2022-11-28', 'User-Agent': 'VoiceBridge-release'})
        try:
            with urllib.request.urlopen(req, timeout=90) as response:
                return json.load(response)
        except urllib.error.HTTPError as error:
            if missing_ok and error.code == 404:
                return None
            if error.code not in (429, 500, 502, 503, 504) or attempt == 4:
                raise
        except (TimeoutError, urllib.error.URLError):
            if attempt == 4:
                raise
        time.sleep(2 ** attempt)

exe = Path(filename).read_bytes()
compressed = gzip.compress(exe, compresslevel=9, mtime=0)
pieces = [compressed[n:n + 2097152] for n in range(0, len(compressed), 2097152)]

def upload(piece):
    result = request('POST', 'git/blobs', {'encoding': 'base64', 'content': base64.b64encode(piece).decode()})
    return {'blob': result['sha'], 'bytes': len(piece), 'sha256': hashlib.sha256(piece).hexdigest()}

with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
    chunks = list(pool.map(upload, pieces))
manifest = {'encoding': 'chunks-gzip-v1', 'version': version, 'exeSha256': hashlib.sha256(exe).hexdigest(), 'bytes': len(exe), 'gzipSha256': hashlib.sha256(compressed).hexdigest(), 'gzipBytes': len(compressed), 'chunks': chunks}
path = 'contents/deployment/client-transfer-' + version + '.json'
previous = request('GET', path, missing_ok=True)
body = {'branch': 'main', 'message': 'Record verified VoiceBridge ' + version + ' deployment chunks', 'content': base64.b64encode(json.dumps(manifest, indent=2).encode()).decode()}
if previous:
    body['sha'] = previous['sha']
request('PUT', path, body)
print(json.dumps({'version': version, 'sha256': manifest['exeSha256'], 'bytes': len(exe), 'chunks': len(chunks)}))
