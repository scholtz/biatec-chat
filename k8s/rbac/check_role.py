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

try:
    import yaml
except ImportError:
    sys.exit("PyYAML is required: pip install pyyaml")

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
    """The verbs the rules grant on (group, resource). With `name`, rules without resourceNames and rules naming it count; without, only
    rules without resourceNames do (a verb that holds for every name)."""
    found = set()
    for r in rules:
        if group not in r.get("apiGroups", []) or resource not in r.get("resources", []):
            continue
        names = r.get("resourceNames")
        if names and name not in names:
            continue
        verbs = set(r.get("verbs", []))
        if names and "/" not in resource:
            # The API server never authorizes create, list or watch of a resource through resourceNames (the request carries no name; a
            # subresource request such as pods/exec does), so such a grant would pass this check and still be refused on the cluster.
            verbs -= {"create", "list", "watch", "deletecollection"}
        found |= verbs
    return found


# Every resource a rule may name: an allow-list, so a new resource (jobs, daemonsets, pods/ephemeralcontainers, ...) is a conscious change.
ALLOWED_RESOURCES = {
    "deployments", "statefulsets", "configmaps", "services", "serviceaccounts", "ingresses", "networkpolicies", "secrets", "pods",
    "persistentvolumeclaims", "events", "pods/log", "pods/exec", "persistentvolumes",
}


def check_rules(rules, label):
    errors = []
    for r in rules:
        for res in r.get("resources", []):
            if res not in ALLOWED_RESOURCES:
                errors.append(f"{label}: resource '{res}' is not one the deploy needs ({r})")
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
        if "secrets" in r.get("resources", []) and not names and not verbs <= {"create"}:
            errors.append(f"{label}: unnamed rules on secrets may only create ({r})")
        if "pods" in r.get("resources", []) and verbs - {"get", "list", "watch"}:
            errors.append(f"{label}: pods may only be read; running new pods can mount any secret ({r})")
        if "pods/exec" in r.get("resources", []) and not names:
            errors.append(f"{label}: pods/exec must be limited to named pods ({r})")
    return errors


# What deploy.yml may run. A call of another shape (kubectl delete, patch, get secret X, apply --prune, rollout restart, exec into a
# deployment, ...) is a conscious change of the Role and of this file.
ALLOWED_GET = {"secret", "pods", "pvc", "pv", "events"}
ALLOWED_VERBS = {"kustomize", "apply", "config", "create", "rollout", "exec", "describe", "logs", "get"}


def kubectl_calls(deploy_yaml):
    """Every `kubectl ...` of deploy.yml as (line, words, raw): shell line continuations joined, cut at the next pipe or separator, and
    the words without flags (and without the values of -n/--namespace/--context/-c/--container), so `kubectl -n "$NAMESPACE" delete ...`,
    swapped flags and a command split over lines look like the plain form."""
    calls = []
    joined = re.sub(r"\\\n\s*", " ", deploy_yaml)
    for n, line in enumerate(joined.splitlines(), 1):
        if line.lstrip().startswith("#"):
            continue
        for m in re.finditer(r"kubectl\s+([^|;&]*)", line):
            raw = m.group(1)
            rest = re.sub(r"(?:^|\s)(?:-n|--namespace|--context|-c|--container)(?:=|\s+)\S+", " ", raw)
            words = [w for w in rest.split() if not w.startswith("-") and w != "--"]
            if words:
                calls.append((n, words, raw))
    return calls


def check_kubectl_calls(deploy_yaml):
    errors = []
    for n, words, raw in kubectl_calls(deploy_yaml):
        verb, a1, a2 = words[0], words[1] if len(words) > 1 else "", words[2] if len(words) > 2 else ""
        where = f"deploy.yml:{n}: 'kubectl {' '.join(words[:3])}'"
        if verb not in ALLOWED_VERBS:
            errors.append(f"{where} is not an operation the deploy Role was written for")
        elif verb == "create" and not (a1 == "secret" and a2 == "generic"):
            errors.append(f"{where}: only 'kubectl create secret generic' is expected")
        elif verb == "apply" and re.search(r"--prune|--all\b|--force\b", raw):
            errors.append(f"{where}: apply with --prune, --all or --force deletes or replaces objects, which the Role does not allow")
        elif verb == "rollout" and a1 != "status":
            errors.append(f"{where}: only 'kubectl rollout status' is expected")
        elif verb == "get" and not set(a1.split(",")) <= ALLOWED_GET:
            errors.append(f"{where} reads a resource the deploy Role was not written for")
        elif verb == "get" and a1 == "secret" and a2 != "biatec-chat-generated":
            errors.append(f"{where} reads a secret other than biatec-chat-generated")
        elif verb == "exec" and not a1.startswith("statefulset/"):
            errors.append(f"{where} is not the statefulset the Role allows exec on")
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
    # Secrets written by the deploy, the pod it execs into and the workloads it waits for.
    calls = [w for _, w, _ in kubectl_calls(deploy_yaml)]
    for secret in sorted({w[3] for w in calls if w[:3] == ["create", "secret", "generic"] and len(w) > 3}):
        missing = {"get", "patch"} - verbs_for(rules, "", "secrets", secret)
        if missing:
            errors.append(f"deploy.yml writes secret {secret} but the Role lacks {sorted(missing)} on it")
    if "create" not in verbs_for(rules, "", "secrets"):
        errors.append("the Role cannot create secrets, which the first deploy does")
    for sts in sorted({w[1].split("/", 1)[1] for w in calls if w[0] == "exec" and len(w) > 1 and w[1].startswith("statefulset/")}):
        if not {"get", "create"} <= verbs_for(rules, "", "pods/exec", sts + "-0"):
            errors.append(f"deploy.yml execs into statefulset/{sts} but the Role does not allow get and create on pods/exec of {sts}-0")
    for w in calls:
        if w[:2] == ["rollout", "status"] and len(w) > 2 and "/" in w[2]:
            kind, name = w[2].split("/", 1)
            kind_res = {"statefulset": "statefulsets", "deployment": "deployments"}.get(kind)
            if kind_res is None or not {"get", "list", "watch"} <= verbs_for(rules, "apps", kind_res, name):
                errors.append(f"deploy.yml waits for {w[2]} but the Role lacks get, list and watch on it")
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
        "unnamed secrets patch": lambda rs: rs.append({"apiGroups": [""], "resources": ["secrets"], "verbs": ["patch"]}),
        "jobs create": lambda rs: rs.append({"apiGroups": ["batch"], "resources": ["jobs"], "verbs": ["create"]}),
        "create through resourceNames": lambda rs: rule(rs, "deployments").update(resourceNames=["biatec-chat-backend"]),
        "pods create": lambda rs: rule(rs, "pods")["verbs"].append("create"),
        "exec without get": lambda rs: rule(rs, "pods/exec", True)["verbs"].remove("get"),
    }
    failed = []
    for bad in ("kubectl delete deployment x", "kubectl patch secret x", "kubectl get secret other -n a", "kubectl create namespace x", "kubectl get nodes",
                "kubectl -n x delete deployment y", "kubectl --namespace=x get secret other", "kubectl apply --prune -f x", "kubectl rollout restart deployment/x", "kubectl exec deployment/b -- env", "kubectl get \\\n  secret other"):
        if not check_kubectl_calls(deploy_yaml + "\n" + bad):
            failed.append(bad)
    for name, fn in cases.items():
        if not mutate(fn):
            failed.append(name)
    extra_exec = deploy_yaml + "\nkubectl exec statefulset/biatec-chat-redis -i -- ps"
    if not check(role, cluster_role, overlays, extra_exec):
        failed.append("exec into redis (flags swapped) not covered")
    wide = copy.deepcopy(cluster_role)
    wide["rules"].append({"apiGroups": [""], "resources": ["nodes"], "verbs": ["get"]})
    if not check(role, wide, overlays, deploy_yaml):
        failed.append("cluster role with nodes")
    if failed:
        print("SELF-TEST FAILED, these were not caught:", ", ".join(failed))
        return 1
    print(f"self-test passed: {len(cases) + 1} bad roles and 11 unexpected kubectl calls are all rejected")
    return 0


if __name__ == "__main__":
    sys.exit(main())
