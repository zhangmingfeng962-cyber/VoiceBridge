#!/bin/bash
set -euo pipefail
umask 077
mode=${1:---prepare}
if [[ "$mode" != --prepare && "$mode" != --enable-public ]]; then echo 'Usage: Configure-ScreenTurn.sh [--prepare|--enable-public]'; exit 2; fi
test "$(id -u)" = 0
: "${VB_TURN_REALM:?Set the approved HTTPS domain}"
: "${VB_PUBLIC_IP:?Set ECS public IPv4 address}"
: "${VB_PRIVATE_IP:?Set ECS primary private IPv4 address}"
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
export VB_TURN_TEMPLATE="$script_dir/../relay/coturn.conf.example"
python3 - <<'PY'
import os,re,ipaddress,pathlib,secrets
realm=os.environ['VB_TURN_REALM']
assert re.fullmatch(r'[a-z0-9](?:[a-z0-9.-]{0,251}[a-z0-9])?',realm) and '.' in realm
for key in ['VB_PUBLIC_IP','VB_PRIVATE_IP']: ipaddress.IPv4Address(os.environ[key])
root=pathlib.Path('/etc/voicebridge-turn');root.mkdir(mode=0o700,parents=True,exist_ok=True)
secret=root/'shared-secret'
if not secret.exists(): secret.write_text(secrets.token_hex(32));secret.chmod(0o600)
value=secret.read_text().strip();assert re.fullmatch('[a-f0-9]{64}',value)
config=pathlib.Path(os.environ['VB_TURN_TEMPLATE']).read_text()
for a,b in {'__REALM__':realm,'__PUBLIC_IP__':os.environ['VB_PUBLIC_IP'],'__PRIVATE_IP__':os.environ['VB_PRIVATE_IP'],'__SECRET__':value}.items():config=config.replace(a,b)
(root/'turnserver.conf.pending').write_text(config)
(root/'relay.env.pending').write_text(f'STUN_URLS=stun:{realm}:3478\nTURN_URLS=turn:{realm}:3478?transport=udp,turn:{realm}:3478?transport=tcp,turns:{realm}:5349?transport=tcp\nTURN_SHARED_SECRET={value}\nSCREEN_VIDEO_BUDGET_BPS=5000000\n')
for name in ['turnserver.conf.pending','relay.env.pending']: (root/name).chmod(0o600)
PY
if [[ "$mode" = --prepare ]]; then echo 'TURN configuration prepared privately. No ports, services or active relay settings changed.'; exit 0; fi
# Explicit activation is for after ICP/HTTPS approval and review of the security-group changes.
: "${VB_TLS_CERT:?Set full certificate chain path}"
: "${VB_TLS_KEY:?Set private key path}"
openssl x509 -in "$VB_TLS_CERT" -noout -checkhost "$VB_TURN_REALM"
openssl x509 -in "$VB_TLS_CERT" -noout -checkend 86400
cert_pub=$(openssl x509 -in "$VB_TLS_CERT" -pubkey -noout | openssl pkey -pubin -outform DER | sha256sum)
key_pub=$(openssl pkey -in "$VB_TLS_KEY" -pubout -outform DER | sha256sum)
test "$cert_pub" = "$key_pub"
if systemctl is-active --quiet coturn.service; then echo 'Existing active coturn requires an explicit maintenance review.'; exit 1; fi
systemctl mask coturn.service
DEBIAN_FRONTEND=noninteractive apt-get -qq update
DEBIAN_FRONTEND=noninteractive NEEDRESTART_MODE=l apt-get -y -qq --no-install-recommends install coturn
stamp=$(date -u +%Y%m%dT%H%M%SZ)
if test -f /etc/turnserver.conf; then cp -a /etc/turnserver.conf "/etc/voicebridge-turn/coturn-before-$stamp.conf"; fi
cp -a /etc/voicebridge/relay.env "/etc/voicebridge-turn/relay-before-$stamp.env"
install -d -o root -g turnserver -m 0750 /etc/voicebridge-turn /etc/voicebridge-turn/tls
install -o root -g turnserver -m 0640 "$VB_TLS_CERT" /etc/voicebridge-turn/tls/fullchain.pem
install -o root -g turnserver -m 0640 "$VB_TLS_KEY" /etc/voicebridge-turn/tls/privkey.pem
install -o root -g turnserver -m 0640 /etc/voicebridge-turn/turnserver.conf.pending /etc/turnserver.conf
bash "$script_dir/Configure-ScreenBandwidth.sh" --install
python3 - <<'PY'
from pathlib import Path
env=Path('/etc/voicebridge/relay.env');keys={'STUN_URLS','TURN_URLS','TURN_SHARED_SECRET','SCREEN_VIDEO_BUDGET_BPS'}
lines=[line for line in env.read_text().splitlines() if line.split('=',1)[0] not in keys]
env.write_text('\n'.join(lines)+'\n'+Path('/etc/voicebridge-turn/relay.env.pending').read_text());env.chmod(0o600)
PY
systemctl unmask coturn.service
systemctl daemon-reload
systemctl enable --now coturn.service
systemctl restart voicebridge.service
echo 'TURN enabled. Verify HTTPS/WSS, direct and forced-relay video before publishing a download.'

