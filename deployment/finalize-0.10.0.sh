#!/bin/bash
set -euo pipefail
umask 077
node=/opt/voicebridge/runtime/node-v22.23.3-linux-x64/bin/node
qos=$(mktemp)
binary=$(mktemp)
trap 'rm -f "$qos" "$binary"' EXIT
curl -fsSL --retry 2 --max-time 90 'https://api.github.com/repos/zhangmingfeng962-cyber/VoiceBridge/contents/scripts/Configure-ScreenBandwidth.sh?ref=540bf563269ffeffac730900e73b2d8557b0928d' | "$node" -e 'let s="";process.stdin.on("data",c=>s+=c);process.stdin.on("end",()=>{const j=JSON.parse(s);if(j.encoding!=="base64")process.exit(1);process.stdout.write(Buffer.from(j.content,"base64"));});' > "$qos"
printf '6ce77891198ad558c4d0d2edcc39b20ac3cda910d466c5b8d2aa423f84325888  %s\n' "$qos" | sha256sum -c -
bash "$qos" --install
bash /usr/local/sbin/voicebridge-turn-bandwidth --apply
systemctl restart voicebridge-turn-bandwidth.service
systemctl is-active --quiet voicebridge-turn-bandwidth.service
echo BANDWIDTH_RESTART_CHECK_OK
curl -fsSL --retry 2 --max-time 240 'https://github.com/zhangmingfeng962-cyber/VoiceBridge/releases/download/v0.10.0/VoiceBridge-v0.10.0.exe' > "$binary"
printf 'aab537626db179ac63324fcf282a916f38491f8c43e9d4ab08a858cccc57a389  %s\n' "$binary" | sha256sum -c -
install -d -m 755 /opt/voicebridge/downloads
if test -f /opt/voicebridge/downloads/VoiceBridge.exe; then cp --update=none /opt/voicebridge/downloads/VoiceBridge.exe /etc/voicebridge/backups/VoiceBridge-before-0.10.0.exe; fi
install -m 644 "$binary" /opt/voicebridge/downloads/.VoiceBridge.exe.new
mv -Tf /opt/voicebridge/downloads/.VoiceBridge.exe.new /opt/voicebridge/downloads/VoiceBridge.exe
echo PUBLIC_CLIENT_INSTALLED
sha256sum /opt/voicebridge/downloads/VoiceBridge.exe
stat -c 'CLIENT_BYTES %s' /opt/voicebridge/downloads/VoiceBridge.exe
RENEWED_LINEAGE=/etc/letsencrypt/live/voicebridge-room.cn /etc/letsencrypt/renewal-hooks/deploy/voicebridge-services
echo CERTIFICATE_SERVICE_HOOK_OK
if certbot renew --dry-run --non-interactive >/var/log/voicebridge-certbot-dry-run.log 2>&1; then
 echo CERTIFICATE_RENEWAL_DRY_RUN_OK
else
 tail -n 15 /var/log/voicebridge-certbot-dry-run.log
 exit 1
fi
systemctl is-active voicebridge nginx coturn voicebridge-turn-bandwidth
systemctl is-enabled voicebridge nginx coturn voicebridge-turn-bandwidth certbot.timer
tc -s actions get action police index 56100
curl -fsS --resolve voicebridge-room.cn:443:127.0.0.1 https://voicebridge-room.cn/health
echo
echo PUBLICATION_FINALIZED
