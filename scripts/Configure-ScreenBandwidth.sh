#!/bin/bash
set -euo pipefail
test "$(id -u)" = 0
mode=${1:---install}
[[ "$mode" == --install || "$mode" == --apply ]] || exit 2
command -v tc >/dev/null
iface=$(ip -4 route show default | awk 'NR==1 {for(i=1;i<=NF;i++)if($i=="dev"){print $(i+1);exit}}')
[[ "$iface" =~ ^[a-zA-Z0-9_.:-]+$ ]] || { echo 'No unambiguous default network interface'; exit 1; }
install -d -m 700 /etc/voicebridge
marker=/etc/voicebridge/turn-qos-managed
if ! test -f "$marker"; then
 for preference in 56100 56101 56102 56103 56104; do
  test -z "$(tc filter show dev "$iface" egress pref "$preference" 2>/dev/null)" || { echo 'Reserved filter preference already in use'; exit 1; }
 done
 if tc actions get action police index 56100 >/dev/null 2>&1; then echo 'Reserved traffic action already in use'; exit 1; fi
 printf '%s\n' "$iface" > "$marker"
 chmod 600 "$marker"
else
 test "$(cat "$marker")" = "$iface" || { echo 'Default network interface changed; review traffic configuration'; exit 1; }
fi
if ! tc qdisc show dev "$iface" | grep -q 'qdisc clsact'; then
 # Add classifier hooks only; preserve the root queue and unrelated traffic rules.
 tc qdisc add dev "$iface" clsact
fi
if tc actions get action police index 56100 >/dev/null 2>&1; then
 tc actions change action police rate 5mbit burst 128k mtu 64kb conform-exceed drop/pipe index 56100
 tc filter replace dev "$iface" egress protocol ip pref 56100 handle 1 flower skip_hw ip_proto udp src_port 3478 action police index 56100
else
 tc filter replace dev "$iface" egress protocol ip pref 56100 handle 1 flower skip_hw ip_proto udp src_port 3478 action police rate 5mbit burst 128k mtu 64kb conform-exceed drop/pipe index 56100
fi
tc filter replace dev "$iface" egress protocol ip pref 56101 handle 1 flower skip_hw ip_proto udp src_port 49160-49259 action police index 56100
tc filter replace dev "$iface" egress protocol ip pref 56102 handle 1 flower skip_hw ip_proto tcp src_port 3478 action police index 56100
tc filter replace dev "$iface" egress protocol ip pref 56103 handle 1 flower skip_hw ip_proto tcp src_port 5349 action police index 56100
tc filter replace dev "$iface" egress protocol ip pref 56104 handle 1 flower skip_hw ip_proto udp src_port 5349 action police index 56100
if [[ "$mode" == --install ]]; then
 install -m 750 "$0" /usr/local/sbin/voicebridge-turn-bandwidth
 cat >/etc/systemd/system/voicebridge-turn-bandwidth.service <<'UNIT'
[Unit]
Description=VoiceBridge TURN aggregate 5 Mbps egress limit
Wants=network-online.target
After=network-online.target
Before=coturn.service
[Service]
Type=oneshot
ExecStart=/usr/local/sbin/voicebridge-turn-bandwidth --apply
RemainAfterExit=yes
[Install]
WantedBy=multi-user.target
UNIT
 systemctl daemon-reload
 systemctl enable --now voicebridge-turn-bandwidth.service
 systemctl is-active --quiet voicebridge-turn-bandwidth.service
fi
echo 'TURN_AGGREGATE_EGRESS_5MBPS_READY'
