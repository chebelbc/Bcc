#!/usr/bin/env python3
"""
Static MilestonePSTools command catalog extractor.

Builds a machine-readable command catalog from a checkout of the official
MilestonePSTools repository (https://github.com/milestonesys/MilestonePSTools)
WITHOUT executing any PowerShell. It is the offline counterpart of
tools/discovery/Export-PSToolsCatalog.ps1, which builds the same schema at
runtime from Get-Command / Get-Help on a Windows machine with the module
installed. The runtime catalog is authoritative for the installed version;
this static catalog is used to (a) seed the application when no runtime
catalog exists yet, (b) produce the inventory/capability documentation, and
(c) diff repository HEAD against an installed version.

Sources merged, in order of authority for each field:
  * docs/commands/en-US/*.md   platyPS reference (syntax, parameter sets,
                               parameter types, pipeline input, accepted
                               values, examples, outputs)
  * MilestonePSTools/Public/**/*.ps1   PowerShell function source
  * src/MilestonePSTools/**/*.cs       compiled cmdlet source
    (SupportsShouldProcess, ConfirmImpact, RequiresVms* attributes,
     MipItemTransformation targets, aliases)
  * MilestonePSTools/deprecations.psd1 deprecated command -> replacement

Nothing here is guessed: every field records where it came from, and
classification rules (risk/category/GUI suitability) live in
classification.py so they are shared with the runtime import path.

Usage:
  python3 extract_repo_catalog.py <repo-root> <out-catalog.json> [<out-report.md>]
"""
import datetime
import json
import os
import re
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import classification  # noqa: E402

SCHEMA_VERSION = "1.0"

# --------------------------------------------------------------------------
# platyPS markdown parsing
# --------------------------------------------------------------------------

def split_sections(md):
    """Split a platyPS document into {'## HEADING': body} preserving order."""
    sections = {}
    current = None
    buf = []
    for line in md.splitlines():
        m = re.match(r"^## (.+?)\s*$", line)
        if m:
            if current is not None:
                sections[current] = "\n".join(buf).strip("\n")
            current = m.group(1).strip().upper()
            buf = []
        else:
            buf.append(line)
    if current is not None:
        sections[current] = "\n".join(buf).strip("\n")
    return sections


def strip_md(text):
    text = re.sub(r"\r", "", text or "")
    text = re.sub(r"\n{3,}", "\n\n", text)
    return text.strip()


def parse_syntax(body):
    """Return list of {name, isDefault, syntax} from the SYNTAX section."""
    sets = []
    current = {"name": "__AllParameterSets", "isDefault": True}
    code = []
    in_code = False
    for line in body.splitlines():
        m = re.match(r"^### (.+?)\s*$", line)
        if m and not in_code:
            name = m.group(1)
            is_default = "(Default)" in name
            name = name.replace("(Default)", "").strip()
            current = {"name": name, "isDefault": is_default}
            continue
        if line.startswith("```"):
            if in_code:
                current["syntax"] = " ".join(l.strip() for l in code).strip()
                sets.append(current)
                current = {"name": current["name"], "isDefault": current["isDefault"]}
                code = []
            in_code = not in_code
            continue
        if in_code:
            code.append(line)
    if len(sets) == 1 and sets[0]["name"] == "__AllParameterSets":
        sets[0]["isDefault"] = True
    if sets and not any(s["isDefault"] for s in sets):
        # platyPS marks the default only when there are several sets
        sets[0]["isDefault"] = len(sets) == 1
    return sets


def parse_yaml_block(text):
    out = {}
    for line in text.splitlines():
        m = re.match(r"^([A-Za-z ]+):\s*(.*)$", line)
        if m:
            out[m.group(1).strip()] = m.group(2).strip()
    return out


def parse_parameters(body):
    params = []
    chunks = re.split(r"^### ", body, flags=re.M)
    for chunk in chunks:
        if not chunk.strip():
            continue
        header, _, rest = chunk.partition("\n")
        header = header.strip()
        if header.lower().startswith("commonparameters"):
            continue
        if not header.startswith("-"):
            continue
        name = header[1:].strip()
        yamls = re.findall(r"```yaml\n(.*?)```", rest, flags=re.S)
        desc = re.split(r"```yaml", rest)[0]
        blocks = [parse_yaml_block(y) for y in yamls]
        if not blocks:
            continue
        first = blocks[0]
        p = {
            "name": name,
            "type": first.get("Type", "Object"),
            "description": strip_md(desc),
            "aliases": [a.strip() for a in first.get("Aliases", "").split(",") if a.strip()],
            "acceptedValues": [a.strip() for a in first.get("Accepted values", "").split(",") if a.strip()],
            "defaultValue": None if first.get("Default value", "None") in ("None", "") else first.get("Default value"),
            "acceptsWildcards": first.get("Accept wildcard characters", "False").lower().startswith("true"),
            "pipelineInput": first.get("Accept pipeline input", "False"),
            "sets": [],
        }
        for b in blocks:
            sets_raw = b.get("Parameter Sets", "(All)")
            required = b.get("Required", "False").lower().startswith("true")
            position = b.get("Position", "Named")
            for s in [x.strip() for x in sets_raw.split(",") if x.strip()]:
                p["sets"].append({
                    "set": "__AllParameterSets" if s == "(All)" else s,
                    "mandatory": required,
                    "position": None if position in ("Named", "") else int(position) if position.isdigit() else position,
                })
        pin = p["pipelineInput"]
        p["valueFromPipeline"] = "ByValue" in pin
        p["valueFromPipelineByPropertyName"] = "ByPropertyName" in pin
        del p["pipelineInput"]
        params.append(p)
    return params


def parse_examples(body):
    examples = []
    chunks = re.split(r"^### ", body, flags=re.M)
    for chunk in chunks:
        if not chunk.strip():
            continue
        title, _, rest = chunk.partition("\n")
        codes = re.findall(r"```(?:powershell|ps1|ps)?\n(.*?)```", rest, flags=re.S)
        remarks = re.sub(r"```.*?```", "", rest, flags=re.S)
        examples.append({
            "title": title.strip(),
            "code": "\n".join(c.strip("\n") for c in codes),
            "remarks": strip_md(remarks)[:4000],
        })
    return examples


def parse_types_list(body):
    types = []
    for line in body.splitlines():
        m = re.match(r"^### (.+?)\s*$", line)
        if m:
            t = m.group(1).strip()
            if t not in types and t.lower() != "none":
                types.append(t)
    return types


def parse_doc(path):
    with open(path, encoding="utf-8-sig", errors="replace") as f:
        md = f.read()
    front = {}
    fm = re.match(r"^---\n(.*?)\n---\n", md, flags=re.S)
    if fm:
        for line in fm.group(1).splitlines():
            if ":" in line:
                k, v = line.split(":", 1)
                front[k.strip()] = v.strip()
    title = re.search(r"^# (.+)$", md, flags=re.M)
    s = split_sections(md)
    desc = strip_md(s.get("DESCRIPTION", ""))
    doc_requirements = re.findall(r"^- (Requires .+|Does not require .+)$", desc, flags=re.M)
    return {
        "name": title.group(1).strip() if title else os.path.splitext(os.path.basename(path))[0],
        "helpUri": front.get("online version"),
        "synopsis": strip_md(s.get("SYNOPSIS", "")),
        "description": desc,
        "docRequirements": doc_requirements,
        "parameterSets": parse_syntax(s.get("SYNTAX", "")),
        "parameters": parse_parameters(s.get("PARAMETERS", "")),
        "examples": parse_examples(s.get("EXAMPLES", "")),
        "inputs": parse_types_list(s.get("INPUTS", "")),
        "outputs": parse_types_list(s.get("OUTPUTS", "")),
        "notes": strip_md(s.get("NOTES", "")),
    }

# --------------------------------------------------------------------------
# Source parsing
# --------------------------------------------------------------------------

def find_matching(text, start, open_ch, close_ch):
    """Index of the bracket matching text[start] (== open_ch); string/comment aware."""
    depth = 0
    i = start
    n = len(text)
    quote = None
    while i < n:
        c = text[i]
        if quote:
            if c == quote:
                if quote == "'" and i + 1 < n and text[i + 1] == "'":
                    i += 2
                    continue
                quote = None
            elif c == "`" and quote == '"':
                i += 2
                continue
        else:
            if c in ("'", '"'):
                quote = c
            elif c == "#" and (i == 0 or text[i - 1] != "<"):
                nl = text.find("\n", i)
                i = n if nl < 0 else nl
                continue
            elif c == "<" and text.startswith("<#", i):
                e = text.find("#>", i)
                i = n if e < 0 else e + 2
                continue
            elif c == open_ch:
                depth += 1
            elif c == close_ch:
                depth -= 1
                if depth == 0:
                    return i
        i += 1
    return -1


def split_top_level(text, sep=","):
    parts, depth, buf, quote = [], 0, [], None
    i = 0
    while i < len(text):
        c = text[i]
        if quote:
            buf.append(c)
            if c == quote:
                quote = None
        elif c in ("'", '"'):
            quote = c
            buf.append(c)
        elif c == "#" and not (i > 0 and text[i - 1] == "<"):
            nl = text.find("\n", i)
            nl = len(text) if nl < 0 else nl
            buf.append(text[i:nl])
            i = nl
            continue
        elif c in "([{":
            depth += 1
            buf.append(c)
        elif c in ")]}":
            depth -= 1
            buf.append(c)
        elif c == sep and depth == 0:
            parts.append("".join(buf))
            buf = []
        else:
            buf.append(c)
        i += 1
    if "".join(buf).strip():
        parts.append("".join(buf))
    return parts


def parse_requirement_attrs(text):
    """Parse RequiresVms*/Requires* attributes (PowerShell or C# syntax)."""
    req = {
        "vmsConnection": None,
        "minVmsVersion": None,
        "vmsFeatures": [],
        "elevation": False,
        "interactiveSession": False,
        "windowsUser": False,
    }
    m = re.search(r"\[RequiresVmsConnection\(\s*(\$?false|\$?true)?\s*\)\]", text, flags=re.I)
    if m:
        req["vmsConnection"] = not (m.group(1) and "false" in m.group(1).lower())
    m = re.search(r"\[RequiresVmsVersion\(\s*['\"]?([0-9][0-9.]*)['\"]?\s*\)\]", text)
    if m:
        req["minVmsVersion"] = m.group(1)
    req["vmsFeatures"] = sorted(set(re.findall(r"\[RequiresVmsFeature\(\s*['\"]([A-Za-z0-9_]+)['\"]\s*\)\]", text)))
    req["elevation"] = bool(re.search(r"\[RequiresElevation\(\)\]", text))
    req["interactiveSession"] = bool(re.search(r"\[RequiresInteractiveSession\(\)\]", text))
    req["windowsUser"] = bool(re.search(r"\[RequiresVmsWindowsUser\(\)\]", text))
    return req


def parse_param_chunk_ps(chunk):
    m = re.search(r"\$([A-Za-z_][A-Za-z0-9_]*)\s*(=.*)?$", chunk.strip(), flags=re.S)
    if not m:
        return None
    attrs = chunk[: m.start()]
    info = {"name": m.group(1)}
    vs = re.search(r"\[ValidateSet\((.*?)\)\]", attrs, flags=re.S)
    if vs:
        info["validateSet"] = re.findall(r"['\"]([^'\"]+)['\"]", vs.group(1))
    mt = re.search(r"\[MipItemTransformation\(\[([A-Za-z0-9_.]+)\]", attrs)
    if mt:
        info["mipItemType"] = mt.group(1).split(".")[-1]
    vit = re.search(r"\[ValidateVmsItemType\((.*?)\)\]", attrs, flags=re.S)
    if vit:
        info["validItemTypes"] = re.findall(r"['\"]([^'\"]+)['\"]", vit.group(1))
    vv = re.search(r"\[ValidateVmsVersion\(\s*['\"]?([0-9.]+)", attrs)
    if vv:
        info["minVmsVersion"] = vv.group(1)
    vf = re.search(r"\[ValidateVmsFeature\(\s*['\"]([A-Za-z0-9_]+)", attrs)
    if vf:
        info["vmsFeature"] = vf.group(1)
    for k in ("ValidateRange", "ValidatePattern", "ValidateNotNullOrEmpty", "ValidateScript", "ValidateCount", "ValidateLength"):
        if "[" + k in attrs:
            info.setdefault("validators", []).append(k)
    return info


def parse_ps_functions(root):
    funcs = {}
    pub = os.path.join(root, "MilestonePSTools", "Public")
    for dirpath, _, files in os.walk(pub):
        for fn in files:
            if not fn.endswith(".ps1"):
                continue
            path = os.path.join(dirpath, fn)
            with open(path, encoding="utf-8-sig", errors="replace") as f:
                text = f.read()
            for m in re.finditer(r"^function\s+([A-Za-z]+-[A-Za-z0-9]+)\s*\{", text, flags=re.M):
                name = m.group(1)
                body_start = m.end() - 1
                body_end = find_matching(text, body_start, "{", "}")
                body = text[body_start: body_end + 1] if body_end > 0 else text[body_start:]
                pm = re.search(r"\bparam\s*\(", body, flags=re.I)
                header = body[: pm.start()] if pm else body[:2000]
                params = {}
                if pm:
                    p_open = pm.end() - 1
                    p_close = find_matching(body, p_open, "(", ")")
                    for chunk in split_top_level(body[p_open + 1: p_close]):
                        info = parse_param_chunk_ps(chunk)
                        if info:
                            params[info["name"].lower()] = info
                cb = re.search(r"\[CmdletBinding\((.*?)\)\]", header, flags=re.S)
                cb_text = cb.group(1) if cb else ""
                ci = re.search(r"ConfirmImpact\s*=\s*['\"]?(High|Medium|Low|None)", cb_text)
                alias = re.findall(r"^\s*\[Alias\(([^)]*)\)\]", header, flags=re.M)
                funcs[name] = {
                    "implementation": "PowerShell",
                    "sourceFile": os.path.relpath(path, root).replace(os.sep, "/"),
                    "supportsShouldProcess": "SupportsShouldProcess" in cb_text,
                    "confirmImpact": ci.group(1) if ci else ("Medium" if "SupportsShouldProcess" in cb_text else None),
                    "requirements": parse_requirement_attrs(header),
                    "aliases": [a for grp in alias for a in re.findall(r"['\"]([^'\"]+)['\"]", grp)],
                    "sourceParameters": params,
                    "usesShouldProcessCall": "ShouldProcess(" in body,
                    # The module exports with `Export-ModuleMember -Function *`, so helper functions nested in
                    # a public file are exported too, even though they are not part of the documented surface.
                    "internalHelper": os.path.splitext(fn)[0].lower() != name.lower(),
                }
    return funcs


VERB_CONST = re.compile(r"Verbs[A-Za-z]+\.([A-Za-z]+)")
NAME_EXPR = re.compile(r'\s*((?:"[A-Za-z0-9]*"|nameof\([A-Za-z0-9_.]+\))(?:\s*\+\s*(?:"[A-Za-z0-9]*"|nameof\([A-Za-z0-9_.]+\)))*)')


def eval_cs_name(expr):
    """Evaluate the small subset of C# used for cmdlet nouns: "Lit" + nameof(X) ..."""
    out = ""
    for part in re.split(r"\s*\+\s*", expr.strip()):
        if part.startswith('"'):
            out += part.strip('"')
        else:
            out += re.match(r"nameof\(([A-Za-z0-9_.]+)\)", part).group(1).split(".")[-1]
    return out


def preceding_block(text, idx):
    """Text between the previous statement boundary ({, } or ; outside [...]) and idx: the attribute block."""
    depth = 0
    i = idx - 1
    while i >= 0:
        c = text[i]
        if c == "]":
            depth += 1
        elif c == "[":
            depth -= 1
        elif c in "{};" and depth == 0:
            break
        i -= 1
    return text[i + 1: idx]


def parse_cs_param_attrs(pattrs, name):
    info = {"name": name}
    vs = re.search(r"\[ValidateSet\((.*?)\)\]", pattrs, flags=re.S)
    if vs:
        info["validateSet"] = re.findall(r'"([^"]+)"', vs.group(1))
    mt = re.search(r"\[MipItemTransformation\(typeof\(([A-Za-z0-9_.]+)\)", pattrs)
    if mt:
        info["mipItemType"] = mt.group(1).split(".")[-1]
    vit = re.search(r"\[ValidateVmsItemType\((.*?)\)\]", pattrs, flags=re.S)
    if vit:
        info["validItemTypes"] = re.findall(r'"([^"]+)"', vit.group(1))
    vv = re.search(r'\[ValidateVmsVersion\(\s*"([0-9.]+)"', pattrs)
    if vv:
        info["minVmsVersion"] = vv.group(1)
    vf = re.search(r'\[ValidateVmsFeature\(\s*"([A-Za-z0-9_]+)"', pattrs)
    if vf:
        info["vmsFeature"] = vf.group(1)
    return info


def parse_cs_cmdlets(root):
    src_root = os.path.join(root, "src", "MilestonePSTools")
    classes = {}
    cmdlets = {}
    for dirpath, _, files in os.walk(src_root):
        for fn in files:
            if not fn.endswith(".cs"):
                continue
            path = os.path.join(dirpath, fn)
            with open(path, encoding="utf-8-sig", errors="replace") as f:
                text = f.read()
            for m in re.finditer(r"^\s*public\s+(?:abstract\s+|sealed\s+|partial\s+)*class\s+([A-Za-z0-9_]+)(?:<[^>]*>)?\s*(?::\s*([A-Za-z0-9_.<>]+))?", text, flags=re.M):
                cls, base = m.group(1), m.group(2)
                attr_block = preceding_block(text, m.start())
                brace = text.find("{", m.end())
                end = find_matching(text, brace, "{", "}") if brace >= 0 else -1
                body = text[brace: end + 1] if end > 0 else ""
                props = {}
                for pmatch in re.finditer(r"public\s+(?:override\s+|virtual\s+|new\s+)*([A-Za-z0-9_.<>\[\],? ]+?)\s+([A-Za-z0-9_]+)\s*\{\s*get", body):
                    pattrs = preceding_block(body, pmatch.start())
                    if "[Parameter" not in pattrs:
                        continue
                    info = parse_cs_param_attrs(pattrs, pmatch.group(2))
                    props[info["name"].lower()] = info
                classes[cls] = {"base": base.split("<")[0].split(".")[-1] if base else None,
                                "attrs": attr_block, "props": props,
                                "file": os.path.relpath(path, root).replace(os.sep, "/")}
    for cls, c in classes.items():
        cm = re.search(r"\[Cmdlet\(\s*(Verbs[A-Za-z]+\.[A-Za-z]+|\"[A-Za-z]+\")\s*,", c["attrs"])
        if not cm:
            continue
        nm = NAME_EXPR.match(c["attrs"], cm.end())
        if not nm:
            continue
        verb_raw = cm.group(1)
        vm = VERB_CONST.match(verb_raw)
        verb = vm.group(1) if vm else verb_raw.strip('"')
        noun = eval_cs_name(nm.group(1))
        close = find_matching(c["attrs"], c["attrs"].index("(", cm.start()), "(", ")")
        rest = c["attrs"][nm.end(): close]
        chain_attrs = c["attrs"]
        props = dict()
        b = cls
        seen = set()
        while b in classes and b not in seen:
            seen.add(b)
            for k, v in classes[b]["props"].items():
                props.setdefault(k, v)
            if b != cls:
                chain_attrs += "\n" + classes[b]["attrs"]
            b = classes[b]["base"]
        ci = re.search(r"ConfirmImpact\s*=\s*ConfirmImpact\.(High|Medium|Low|None)", rest)
        ssp = re.search(r"SupportsShouldProcess\s*=\s*true", rest) is not None
        chain = []
        b = cls
        while b in classes and b not in chain:
            chain.append(b)
            b = classes[b]["base"]
        ot = re.search(r"\[OutputType\(typeof\(([A-Za-z0-9_.]+)\)", c["attrs"])
        if "SetDeviceCommandBase" in chain and ot and "inputobject" in props:
            # SetDeviceCommandBase.BeginProcessing sets RequiredType from the cmdlet's [OutputType], and
            # rejects InputObject items of any other type (src/.../DeviceCommands/SetDeviceCommandBase.cs).
            props["inputobject"] = dict(props["inputobject"], validItemTypes=[ot.group(1).split(".")[-1]],
                                        validItemTypesSource="SetDeviceCommandBase.RequiredType = OutputType")
        aliases = []
        for am in re.finditer(r"\[Alias\((.*?)\)\]", c["attrs"], flags=re.S):
            aliases += re.findall(r'"([^"]+)"', am.group(1))
        name = f"{verb}-{noun}"
        cmdlets[name] = {
            "implementation": "C#",
            "sourceFile": c["file"],
            "className": cls,
            "supportsShouldProcess": ssp,
            "confirmImpact": ci.group(1) if ci else ("Medium" if ssp else None),
            "requirements": parse_requirement_attrs(chain_attrs),
            "aliases": aliases,
            "sourceParameters": props,
        }
    return cmdlets


def parse_deprecations(root):
    path = os.path.join(root, "MilestonePSTools", "deprecations.psd1")
    out = {}
    if os.path.exists(path):
        with open(path, encoding="utf-8-sig", errors="replace") as f:
            for m in re.finditer(r"'([^']+)'\s*=\s*'([^']+)'", f.read()):
                out[m.group(1)] = m.group(2)
    return out


def git_info(root):
    def run(*args):
        try:
            return subprocess.check_output(["git", "-C", root] + list(args), text=True).strip()
        except Exception:
            return None
    return {"commit": run("rev-parse", "HEAD"), "commitDate": run("log", "-1", "--format=%cI"),
            "remote": run("remote", "get-url", "origin")}


def latest_released_version(root):
    path = os.path.join(root, "docs", "changelog.md")
    if not os.path.exists(path):
        return None, None
    with open(path, encoding="utf-8") as f:
        for line in f:
            m = re.match(r"^## \[(\d+\.\d+\.\d+)\]\s+(\d{4}-\d{2}-\d{2})", line)
            if m:
                return m.group(1), m.group(2)
    return None, None

# --------------------------------------------------------------------------
# Merge
# --------------------------------------------------------------------------

def build_catalog(root):
    docs_dir = os.path.join(root, "docs", "commands", "en-US")
    docs = {}
    for fn in sorted(os.listdir(docs_dir)):
        if not re.match(r"^[A-Z][A-Za-z]+-[A-Za-z0-9]+\.md$", fn):
            continue  # skips about_*, index.md and tooling files such as .markdownlint
        d = parse_doc(os.path.join(docs_dir, fn))
        docs[d["name"]] = d
    funcs = parse_ps_functions(root)
    cmdlets = parse_cs_cmdlets(root)
    deprecations = parse_deprecations(root)
    source = {}
    source.update(cmdlets)
    source.update(funcs)

    with open(os.path.join(root, "version.json"), encoding="utf-8") as f:
        version_json = json.load(f)
    released, released_date = latest_released_version(root)

    names = sorted(set(docs) | set(source), key=str.lower)
    commands = []
    discrepancies = []
    for name in names:
        d = docs.get(name)
        s = source.get(name)
        if d is None:
            discrepancies.append({"command": name, "issue": "Exported by source but has no reference documentation"})
        if s is None:
            discrepancies.append({"command": name, "issue": "Documented but no matching source definition found by the static parser"})
        verb, _, noun = name.partition("-")
        params = []
        for p in (d["parameters"] if d else []):
            sp = (s or {}).get("sourceParameters", {}).get(p["name"].lower(), {})
            p = dict(p)
            if sp.get("validateSet") and not p["acceptedValues"]:
                p["acceptedValues"] = sp["validateSet"]
            for k in ("mipItemType", "validItemTypes", "validItemTypesSource", "minVmsVersion", "vmsFeature", "validators"):
                if k in sp:
                    p[k] = sp[k]
            p["mandatoryInAnySet"] = any(x["mandatory"] for x in p["sets"])
            params.append(p)
        sets = d["parameterSets"] if d else []
        for ps in sets:
            ps["parameters"] = [
                {"name": p["name"],
                 "mandatory": next((x["mandatory"] for x in p["sets"] if x["set"] in (ps["name"], "__AllParameterSets")), False),
                 "position": next((x["position"] for x in p["sets"] if x["set"] in (ps["name"], "__AllParameterSets")), None)}
                for p in params
                if any(x["set"] in (ps["name"], "__AllParameterSets") for x in p["sets"])
            ]
        req = (s or {}).get("requirements") or {}
        cmd = {
            "name": name,
            "verb": verb,
            "noun": noun,
            "commandType": "Cmdlet" if (s or {}).get("implementation") == "C#" else "Function" if s else "Unknown",
            "implementation": (s or {}).get("implementation"),
            "sourceFile": (s or {}).get("sourceFile"),
            "helpUri": d["helpUri"] if d else None,
            "synopsis": d["synopsis"] if d else "",
            "description": d["description"] if d else "",
            "aliases": sorted(set((s or {}).get("aliases", []))),
            "deprecated": name in deprecations,
            "internalHelper": bool((s or {}).get("internalHelper")),
            "replacedBy": deprecations.get(name),
            "supportsShouldProcess": bool((s or {}).get("supportsShouldProcess")),
            "confirmImpact": (s or {}).get("confirmImpact"),
            "requirements": {
                "vmsConnection": req.get("vmsConnection"),
                "minVmsVersion": req.get("minVmsVersion"),
                "vmsFeatures": req.get("vmsFeatures", []),
                "elevation": req.get("elevation", False),
                "interactiveSession": req.get("interactiveSession", False),
                "windowsUser": req.get("windowsUser", False),
                "documented": d["docRequirements"] if d else [],
            },
            "parameterSets": sets,
            "parameters": params,
            "inputs": d["inputs"] if d else [],
            "outputTypes": d["outputs"] if d else [],
            "examples": d["examples"] if d else [],
        }
        commands.append(cmd)

    return {
        "schemaVersion": SCHEMA_VERSION,
        "source": {
            "kind": "static-repository",
            "repository": "https://github.com/milestonesys/MilestonePSTools",
            **git_info(root),
            "versionJson": version_json.get("version"),
            "latestReleasedVersionInChangelog": released,
            "latestReleaseDate": released_date,
            "note": "Generated from repository source and reference docs without executing PowerShell. "
                    "The runtime catalog from Export-PSToolsCatalog.ps1 is authoritative for an installed module.",
        },
        "moduleName": "MilestonePSTools",
        "moduleVersion": released,
        "generatedAtUtc": datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0).isoformat(),
        "deprecations": deprecations,
        "discrepancies": discrepancies,
        "commands": commands,
    }


def main(argv):  # noqa: C901
    if len(argv) < 3:
        print(__doc__)
        return 2
    root, out = argv[1], argv[2]
    catalog = classification.apply_classification(build_catalog(root))
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    with open(out, "w", encoding="utf-8") as f:
        json.dump(catalog, f, indent=1, ensure_ascii=False)
        f.write("\n")
    print(f"Wrote {len(catalog['commands'])} commands to {out}")
    if len(argv) > 3:
        import report
        report.write_report(catalog, argv[3])
        print(f"Wrote report to {argv[3]}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
