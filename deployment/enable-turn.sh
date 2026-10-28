#!/bin/bash
set -euo pipefail
[[ "${1:-}" == --approved-public ]] || exit 2
umask 077
node=/opt/voicebridge/runtime/node-v22.23.3-linux-x64/bin/node
install -d -m 755 /opt/voicebridge/deployment/scripts /opt/voicebridge/deployment/relay
for name in Configure-ScreenTurn.sh coturn.conf.example; do
 if [[ "$name" == Configure-ScreenTurn.sh ]]; then target=/opt/voicebridge/deployment/scripts/$name; else target=/opt/voicebridge/deployment/relay/$name; fi
 curl -fsSL --retry 2 --max-time 90 "https://api.github.com/repos/zhangmingfeng962-cyber/VoiceBridge/contents/deployment/$name?ref=0a84e2640b5a6bafcbd335e24114c36c9bf483ed" | "$node" -e 'let s="";process.stdin.on("data",c=>s+=c);process.stdin.on("end",()=>{const j=JSON.parse(s);if(j.encoding!=="base64")process.exit(1);process.stdout.write(Buffer.from(j.content,"base64"));});' > "$target"
 test -s "$target"
 chmod 600 "$target"
done
export VB_TURN_REALM=voicebridge-room.cn VB_PUBLIC_IP=121.196.145.43 VB_PRIVATE_IP=172.30.161.52
export VB_TLS_CERT=/etc/letsencrypt/live/voicebridge-room.cn/fullchain.pem VB_TLS_KEY=/etc/letsencrypt/live/voicebridge-room.cn/privkey.pem
bash /opt/voicebridge/deployment/scripts/Configure-ScreenTurn.sh --enable-public
curl -fsS --retry 10 --retry-connrefused --retry-delay 1 --max-time 5 http://127.0.0.1:3000/health
printf '\nTURN_SERVICE '; systemctl is-active coturn
printf '\nTURN_LISTENERS\n'; ss -lunt | awk 'NR==1 || /:(3478|5349) /'
printf 'TURN_ENABLE_COMPLETE\n'
