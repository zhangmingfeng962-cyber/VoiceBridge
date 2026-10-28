#!/bin/bash
set -euo pipefail
[[ "${1:-}" == --approved-public ]] || { echo 'Explicit public activation approval is required'; exit 2; }
umask 077
node=/opt/voicebridge/runtime/node-v22.23.3-linux-x64/bin/node
domain=voicebridge-room.cn
test -f /opt/voicebridge/releases/0.10.0-web1/relay/server.mjs
install -d -m 755 /var/www/voicebridge-acme/.well-known/acme-challenge
install -d -m 700 /etc/voicebridge/backups
cp --update=none /etc/nginx/nginx.conf /etc/voicebridge/backups/nginx-before-https.conf
cat >/etc/nginx/nginx.conf <<'NGINX'
user www-data;
worker_processes 1;
pid /run/nginx.pid;
error_log /var/log/nginx/error.log warn;
events { worker_connections 512; }
http {
 server_tokens off;
 access_log off;
 map $http_upgrade $connection_upgrade { default upgrade; '' close; }
 server {
  listen 80;
  server_name voicebridge-room.cn;
  location /.well-known/acme-challenge/ { root /var/www/voicebridge-acme; default_type text/plain; }
  location / { return 404; }
 }
 server {
  listen 127.0.0.1:8080;
  server_name voicebridge-room.cn;
  location / {
   proxy_pass http://127.0.0.1:3000;
   proxy_http_version 1.1;
   proxy_set_header Host voicebridge-room.cn;
   proxy_set_header Upgrade $http_upgrade;
   proxy_set_header Connection $connection_upgrade;
   proxy_read_timeout 90s;
  }
 }
}
NGINX
nginx -t
systemctl reload nginx
if ! test -s /etc/letsencrypt/live/voicebridge-room.cn/fullchain.pem; then
 certbot certonly --webroot --webroot-path /var/www/voicebridge-acme --cert-name "$domain" -d "$domain" --non-interactive --agree-tos --register-unsafely-without-email
fi
openssl x509 -in /etc/letsencrypt/live/voicebridge-room.cn/fullchain.pem -noout -checkhost "$domain"
openssl x509 -in /etc/letsencrypt/live/voicebridge-room.cn/fullchain.pem -noout -checkend 86400
printf 'CERTIFICATE_VALIDATED\n'
fetch_source() {
 local name=$1 destination=$2
 curl -fsSL --retry 2 --max-time 90 "https://api.github.com/repos/zhangmingfeng962-cyber/VoiceBridge/contents/deployment/$name?ref=73ea01901ac273fabaecdc772e88bb651b5c8ee9" | "$node" -e 'let s="";process.stdin.on("data",c=>s+=c);process.stdin.on("end",()=>{const j=JSON.parse(s);if(j.encoding!=="base64")process.exit(1);process.stdout.write(Buffer.from(j.content,"base64"));});' > "$destination"
 test -s "$destination"
}
fetch_source proxy-room.inc /etc/voicebridge/proxy-room.inc
fetch_source nginx-public.conf /etc/voicebridge/nginx-public.pending.conf
chmod 600 /etc/voicebridge/proxy-room.inc /etc/voicebridge/nginx-public.pending.conf
install -d -m 755 /opt/voicebridge/downloads
nginx -t -c /etc/voicebridge/nginx-public.pending.conf
old_release=$(readlink -f /opt/voicebridge/current)
printf '%s\n' "$old_release" > /etc/voicebridge/backups/release-before-public
find /opt/voicebridge/releases/0.10.0-web1/relay -type d -exec chmod 755 {} +
find /opt/voicebridge/releases/0.10.0-web1/relay -type f -exec chmod 644 {} +
ln -s /opt/voicebridge/releases/0.10.0-web1 /opt/voicebridge/current.pending
mv -Tf /opt/voicebridge/current.pending /opt/voicebridge/current
if ! systemctl restart voicebridge || ! curl -fsS --retry 10 --retry-connrefused --retry-delay 1 --max-time 5 http://127.0.0.1:3000/health; then
 ln -s "$old_release" /opt/voicebridge/current.rollback
 mv -Tf /opt/voicebridge/current.rollback /opt/voicebridge/current
 systemctl restart voicebridge
 exit 1
fi
install -m 644 /etc/voicebridge/nginx-public.pending.conf /etc/nginx/nginx.conf
nginx -t
systemctl reload nginx
install -d -m 755 /etc/letsencrypt/renewal-hooks/deploy
cat >/etc/letsencrypt/renewal-hooks/deploy/voicebridge-services <<'HOOK'
#!/bin/bash
set -euo pipefail
[[ "${RENEWED_LINEAGE:-}" == /etc/letsencrypt/live/voicebridge-room.cn ]] || exit 0
nginx -t
systemctl reload nginx
if systemctl is-active --quiet coturn; then
 install -o root -g turnserver -m 640 "$RENEWED_LINEAGE/fullchain.pem" /etc/voicebridge-turn/tls/fullchain.pem
 install -o root -g turnserver -m 640 "$RENEWED_LINEAGE/privkey.pem" /etc/voicebridge-turn/tls/privkey.pem
 systemctl restart coturn
fi
HOOK
chmod 750 /etc/letsencrypt/renewal-hooks/deploy/voicebridge-services
systemctl enable --now certbot.timer
curl -fsS --resolve "$domain:443:127.0.0.1" "https://$domain/health"
printf '\nWEBSITE_HTTPS_ACTIVE\n'
