#!/usr/bin/env python3
"""Checks the deploy identity's RBAC (BCHAT-020) against what the deploy actually does.

    python k8s/rbac/check_role.py              # needs `kubectl kustomize`; run by ci.yml next to the overlay render
    python k8s/rbac/check_role.py --self-test  # proves the checks fail on a role that is too wide or too narrow

It fails when
  * a resource of the rendered overlays k8s/stage and k8s/main is not covered with get, create and patch (that is `kubectl apply`),
  * a secret that deploy.yml writes, or the pod it execs into, is not named in the Role,
  * the Role has a wildcard, a delete, any RBAC object, a secrets list/watch, an unnamed secrets get, an unnamed pods/exec,
    attach, portforward or proxy, or a service account token,
  * the cluster role grants more than the stage PersistentVolumes by name.
"""
import os
import re
import subprocess
import sys

import yaml

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
RBAC = os.path.join(ROOT, "k8s", "rbac")
APPLY_VERBS = {"get", "create", "patch"}
# kind -> (apiGroup, resource) of everything the overlays may contain; an unknown kind is an error, so a new kind is a conscious change.
RESOURCES = {
    "ConfigMap": ("", "configmaps"),
    "Service": ("", "services"),
    "ServiceAccount": ("", "serviceaccounts"),
    "Secret": ("", "secrets"),
    "Deployment": ("apps", "deployments"),
    "StatefulSet": ("apps", "statefulsets"),
    "Ingress": ("networking.k8s.io", "ingresses"),
    "NetworkPolicy": ("networking.k8s.io", "networkpolicies"),
}
FORBIDDEN_VERBS = {"*", "delete", "deletecollection", "escalate", "bind", "impersonate", "update"}
FORBIDDEN_RESOURCES = {
    "*", "roles", "rolebindings", "clusterroles", "clusterrolebindings", "pods/attach", "pods/portforward", "pods/proxy",
    "serviceaccounts/token", "nodes", "namespaces", "customresourcedefinitions", "validatingwebhookconfigurations",
    "mutatingwebhookconfigurations", "certificatesigningrequests",
}


def load_all(path):
    with open(path, encoding="utf-8") as f:
        return [d for d in yaml.safe_load_all(f) if d]


def rendered(overlay):
    out = subprocess.run(["kubectl", "kustomize", os.path.join(ROOT, "k8s", overlay)], capture_output=True, text=True, check=True).stdout
    return [d for d in yaml.safe_load_all(out) if d]


def verbs_for(rules, group, resource, name=None):
    """The verbs the rules grant on (group, resource), for `name` when given (a rule with resourceNames then only counts for it)."""
    found = set()
    for r in rules:
        if group not in r.get("apiGroups", []) or resource not in r.get("resources", []):
            continue
        names = r.get("resourceNames")
        if names and name not in names:
            continue
        found |= set(r.get("verbs", []))
    return found


def check_rules(rules, label):
    errors = []
    for r in rules:
        for v in r.get("verbs", []):
            if v in FORBIDDEN_VERBS:
                errors.append(f"{label}: verb '{v}' is not allowed ({r})")
        for res in r.get("resources", []):
            if res in FORBIDDEN_RESOURCES:
                errors.append(f"{label}: resource '{res}' is not allowed ({r})")
        for g in r.get("apiGroups", []):
            if g == "*":
                errors.append(f"{label}: apiGroup '*' is not allowed ({r})")
        verbs, names = set(r.get("verbs", [])), r.get("resourceNames")
        if "secrets" in r.get("resources", []):
            if verbs & {"list", "watch"}:
                errors.append(f"{label}: secrets must not be listed or watched ({r})")
            if "get" in verbs and not names:
                errors.append(f"{label}: reading secrets must be limited to named secrets ({r})")
        if "pods/exec" in r.get("resources", []) and not names:
            errors.append(f"{label}: pods/exec must be limited to named pods ({r})")
    return errors


# What deploy.yml may run. The checks above find secrets, exec targets and rollouts by their usual spelling; this allow-list makes sure a
# call of another shape (kubectl delete, patch, get secret X, exec into a deployment, ...) is a conscious change of the Role and of this file.
ALLOWED_GET = {"secret", "pods", "pvc", "pv", "events"}
ALLOWED_VERBS = {"kustomize", "apply", "config", "create", "rollout", "exec", "describe", "logs", "get"}


def check_kubectl_calls(deploy_yaml):
    errors = []
    for n, line in enumerate(deploy_yaml.splitlines(), 1):
        if line.lstrip().startswith("#"):
            continue
        for m in re.finditer(r"kubectl\s+([a-z][\w-]*)(?:\s+([^\s|;&]+))?(?:\s+([^\s|;&]+))?", line):
            verb, a1, a2 = m.group(1), m.group(2) or "", m.group(3) or ""
            if verb not in ALLOWED_VERBS:
                errors.append(f"deploy.yml:{n}: 'kubectl {verb}' is not an operation the deploy Role was written for")
            elif verb == "create" and not (a1 == "secret" and a2 == "generic"):
                errors.append(f"deploy.yml:{n}: only 'kubectl create secret generic' is expected, got 'kubectl create {a1}'")
            elif verb == "get" and not set(a1.split(",")) <= ALLOWED_GET:
                errors.append(f"deploy.yml:{n}: 'kubectl get {a1}' reads a resource the deploy Role was not written for")
            elif verb == "get" and a1 == "secret" and a2 != "biatec-chat-generated":
                errors.append(f"deploy.yml:{n}: 'kubectl get secret {a2}' reads a secret other than biatec-chat-generated")
    return errors


def check(role, cluster_role, overlays, deploy_yaml):
    errors = check_rules(role["rules"], "Role")
    rules = role["rules"]
    # Everything the overlays contain can be applied.
    for overlay, docs in overlays.items():
        for d in docs:
            kind, name = d["kind"], d["metadata"]["name"]
            if kind == "PersistentVolume":
                continue
            if kind not in RESOURCES:
                errors.append(f"{overlay}: kind {kind} ({name}) is not known to check_role.py; decide whether the deploy identity may apply it")
                continue
            group, resource = RESOURCES[kind]
            missing = APPLY_VERBS - verbs_for(rules, group, resource, name)
            if missing:
                errors.append(f"{overlay}: the Role lacks {sorted(missing)} on {resource} (needed to apply {kind}/{name})")
    # Secrets written by the deploy, and the pod it execs into.
    for secret in sorted(set(re.findall(r"kubectl create secret generic ([\w.-]+)", deploy_yaml))):
        missing = {"get", "patch"} - verbs_for(rules, "", "secrets", secret)
        if missing:
            errors.append(f"deploy.yml writes secret {secret} but the Role lacks {sorted(missing)} on it")
    if "create" not in verbs_for(rules, "", "secrets"):
        errors.append("the Role cannot create secrets, which the first deploy does")
    for sts in sorted(set(re.findall(r"kubectl exec -i statefulset/([\w.-]+)", deploy_yaml))):
        if not {"get", "create"} <= verbs_for(rules, "", "pods/exec", sts + "-0"):
            errors.append(f"deploy.yml execs into statefulset/{sts} but the Role does not allow get and create on pods/exec of {sts}-0")
    for sts in sorted(set(re.findall(r"kubectl rollout status (?:statefulset|deployment)/([\w.-]+)", deploy_yaml))):
        kind_res = "statefulsets" if f"statefulset/{sts}" in deploy_yaml else "deployments"
        if not {"get", "list", "watch"} <= verbs_for(rules, "apps", kind_res, sts):
            errors.append(f"deploy.yml waits for {kind_res}/{sts} but the Role lacks get, list and watch on it")
    errors += check_kubectl_calls(deploy_yaml)
    # The cluster role: persistent volumes of stage by name, nothing else.
    crules = cluster_role["rules"]
    errors += check_rules(crules, "ClusterRole")
    for r in crules:
        if r.get("resources") != ["persistentvolumes"]:
            errors.append(f"ClusterRole: only persistentvolumes may be granted ({r})")
    stage_pvs = [d["metadata"]["name"] for d in overlays.get("k8s/stage", []) if d["kind"] == "PersistentVolume"]
    if not stage_pvs:
        errors.append("k8s/stage renders no PersistentVolume, so the cluster role is not needed (or the overlay changed)")
    for pv in stage_pvs:
        missing = {"get", "patch"} - verbs_for(crules, "", "persistentvolumes", pv)
        if missing:
            errors.append(f"ClusterRole: lacks {sorted(missing)} on PersistentVolume {pv}")
    if "create" not in verbs_for(crules, "", "persistentvolumes"):
        errors.append("ClusterRole: cannot create the stage PersistentVolumes")
    for d in overlays.get("k8s/main", []):
        if d["kind"] == "PersistentVolume":
            errors.append(f"k8s/main renders PersistentVolume {d['metadata']['name']}: add a cluster role for it before the first production deploy")
    return errors


def main():
    role = load_all(os.path.join(RBAC, "deploy-role.yaml"))[0]
    cluster_role = load_all(os.path.join(RBAC, "deploy-pv-clusterrole.yaml"))[0]
    with open(os.path.join(ROOT, ".github", "workflows", "deploy.yml"), encoding="utf-8") as f:
        deploy_yaml = f.read()
    overlays = {o: rendered(o.split("/", 1)[1]) for o in ("k8s/stage", "k8s/main")}
    if "--self-test" in sys.argv:
        return self_test(role, cluster_role, overlays, deploy_yaml)
    errors = check(role, cluster_role, overlays, deploy_yaml)
    for e in errors:
        print("ERROR:", e)
    if not errors:
        print("deploy RBAC covers the rendered overlays and grants nothing wider")
    return 1 if errors else 0


def self_test(role, cluster_role, overlays, deploy_yaml):
    import copy
    assert not check(role, cluster_role, overlays, deploy_yaml), "the committed role must pass"

    def mutate(fn):
        r = copy.deepcopy(role)
        fn(r["rules"])
        return check(r, cluster_role, overlays, deploy_yaml)

    def rule(rules, resource, names=None):
        return next(x for x in rules if resource in x["resources"] and bool(x.get("resourceNames")) == bool(names))

    cases = {
        "wildcard verb": lambda rs: rs[0]["verbs"].append("*"),
        "delete": lambda rs: rs[0]["verbs"].append("delete"),
        "secrets list": lambda rs: rule(rs, "secrets", True)["verbs"].append("list"),
        "unnamed secrets get": lambda rs: rs.append({"apiGroups": [""], "resources": ["secrets"], "verbs": ["get"]}),
        "unnamed pods/exec": lambda rs: rule(rs, "pods/exec", True).pop("resourceNames"),
        "rolebindings": lambda rs: rs.append({"apiGroups": ["rbac.authorization.k8s.io"], "resources": ["rolebindings"], "verbs": ["create"]}),
        "service account token": lambda rs: rs.append({"apiGroups": [""], "resources": ["serviceaccounts/token"], "verbs": ["create"]}),
        "ingress not applicable": lambda rs: rule(rs, "ingresses")["verbs"].remove("patch"),
        "another exec target": lambda rs: rule(rs, "pods/exec", True).update(resourceNames=["other-0"]),
        "a deploy secret missing": lambda rs: rule(rs, "secrets", True).update(resourceNames=["biatec-chat-generated"]),
        "statefulset watch removed": lambda rs: rule(rs, "statefulsets")["verbs"].remove("watch"),
        "exec without get": lambda rs: rule(rs, "pods/exec", True)["verbs"].remove("get"),
    }
    failed = []
    for bad in ("kubectl delete deployment x", "kubectl patch secret x", "kubectl get secret other -n a", "kubectl create namespace x", "kubectl get nodes"):
        if not check_kubectl_calls(deploy_yaml + "\n" + bad):
            failed.append(bad)
    for name, fn in cases.items():
        if not mutate(fn):
            failed.append(name)
    wide = copy.deepcopy(cluster_role)
    wide["rules"].append({"apiGroups": [""], "resources": ["nodes"], "verbs": ["get"]})
    if not check(role, wide, overlays, deploy_yaml):
        failed.append("cluster role with nodes")
    if failed:
        print("SELF-TEST FAILED, these were not caught:", ", ".join(failed))
        return 1
    print(f"self-test passed: {len(cases) + 1} bad roles and 5 unexpected kubectl calls are all rejected")
    return 0


if __name__ == "__main__":
    sys.exit(main())
