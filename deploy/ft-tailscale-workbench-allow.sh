#!/bin/bash
# 阿里云 Workbench 的来源 100.104.0.0/16 落在 Tailscale 的 100.64.0.0/10 网段里，
# tailscaled 在 ts-input 顶部插的 DROP 会把 Workbench 的 SSH SYN 丢掉（v182 实测：
# 抓包能看到 SYN 到达网卡，服务器却不回 SYN-ACK）。tailscaled 每次启动都会重建这张
# 表，所以要在它启动之后把放行规则补回第一条；链还没建好时重试，避免 ExecStartPost 太早。
set -eu
for i in $(seq 1 30); do
	if iptables -L ts-input -n >/dev/null 2>&1; then
		iptables -C ts-input -s 100.104.0.0/16 -j ACCEPT 2>/dev/null \
			|| iptables -I ts-input 1 -s 100.104.0.0/16 -j ACCEPT
		exit 0
	fi
	sleep 1
done
exit 1
