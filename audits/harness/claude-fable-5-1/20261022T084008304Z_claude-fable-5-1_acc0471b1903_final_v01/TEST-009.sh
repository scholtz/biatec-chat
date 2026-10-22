#!/usr/bin/env bash
# TEST-009 (BCHAT-005) Every Kubernetes overlay must configure trusted proxies for X-Forwarded-For, otherwise all
# per-IP limits key on the ingress pod. Fails while no ForwardedHeaders__KnownNetworks/KnownProxies key is present.
TEST_ID=$(basename "$0" .sh); source "$(dirname "$0")/lib.sh"
for o in k8s/main k8s/stage; do
  ggrep -qE "ForwardedHeaders__(KnownNetworks|KnownProxies)" -- "$o" k8s/base || fail TEST-009 "$o: no ForwardedHeaders__KnownNetworks/KnownProxies (BCHAT-005 open)"
done
pass TEST-009 "forwarded-headers trust configured in every overlay"
