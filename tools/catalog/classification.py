"""
Command classification shared by the static extractor and report generator.

The rules themselves live in catalog/classification-rules.json, which is also
embedded into MilestoneAdmin.Domain and evaluated by CommandClassifier.cs so
that a catalog generated at runtime on Windows is classified identically.
Keep the algorithm here and in CommandClassifier.cs in lockstep; the C# test
suite re-classifies the committed static catalog and fails on any drift.
"""
import json
import os
import re

_RULES_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "catalog", "classification-rules.json")
with open(_RULES_PATH, encoding="utf-8") as _f:
    RULES = json.load(_f)

_RISK_ORDER = [r["id"] for r in RULES["riskLevels"]]
_SECRET_TYPES = {t.lower() for t in RULES["secretParameter"]["types"]}
_SECRET_NAME = re.compile(RULES["secretParameter"]["namePattern"])
_PATH_NAME = re.compile(RULES["pathParameterNamePattern"])
_UNSUPPORTED = {t.lower() for t in RULES["unsupportedParameterTypes"]}
_OBJECT_TYPES = RULES["objectTypes"]
_CATEGORIES = [(c["id"], re.compile(c["nounPattern"])) for c in RULES["categories"]]

PRIMITIVE_CONTROLS = {
    "string": "Text", "char": "Text", "uri": "Uri", "ipaddress": "Text",
    "int16": "Integer", "int32": "Integer", "int64": "Integer", "uint32": "Integer", "uint64": "Integer", "byte": "Integer",
    "double": "Decimal", "single": "Decimal", "decimal": "Decimal",
    "boolean": "Toggle", "switchparameter": "Checkbox",
    "datetime": "DateTime", "timespan": "TimeSpan", "guid": "Guid",
    "pscredential": "Credential", "securestring": "SecureText",
    "hashtable": "KeyValue", "idictionary": "KeyValue",
}


def _base_type(t):
    t = (t or "Object").strip()
    is_array = t.endswith("[]")
    return t[:-2] if is_array else t, is_array


def is_secret_parameter(p):
    t = (p.get("type") or "").lower()
    return t in _SECRET_TYPES or bool(_SECRET_NAME.search(p.get("name", "")))


def _all_known(item_types):
    return bool(item_types) and all(t in _OBJECT_TYPES for t in item_types)


def object_types_for_parameter(p):
    """XProtect object types a selector for this parameter should offer."""
    base, _ = _base_type(p.get("type"))
    if base in _OBJECT_TYPES:
        return [base]
    if p.get("mipItemType") in _OBJECT_TYPES:
        return [p["mipItemType"]]
    if _all_known(p.get("validItemTypes")):
        return list(p["validItemTypes"])
    return []


def control_for_parameter(p):
    """Map a parameter to the UI control kind used by the dynamic form generator."""
    base, is_array = _base_type(p.get("type"))
    lb = base.lower()
    if p.get("type", "").lower() in _UNSUPPORTED or lb in _UNSUPPORTED:
        return "Unsupported"
    if p.get("acceptedValues"):
        return "MultiSelect" if is_array else "Dropdown"
    if base in _OBJECT_TYPES or p.get("mipItemType") in _OBJECT_TYPES or _all_known(p.get("validItemTypes")):
        return "ObjectMultiSelector" if is_array else "ObjectSelector"
    if lb == "string" and _PATH_NAME.match(p.get("name", "")):
        return "FilePath"
    if lb in PRIMITIVE_CONTROLS:
        ctl = PRIMITIVE_CONTROLS[lb]
        if is_array:
            return {"Text": "TextList", "Integer": "IntegerList", "Guid": "GuidList", "Uri": "TextList"}.get(ctl, "Unsupported")
        return ctl
    return "Unsupported"


def _raise(a, b):
    return a if _RISK_ORDER.index(a) >= _RISK_ORDER.index(b) else b


def risk_for(cmd):
    ov = RULES["commandRiskOverrides"].get(cmd["name"])
    if ov:
        return {"level": ov["risk"], "source": "override", "reason": ov["reason"]}
    level = RULES["verbRisk"].get(cmd["verb"])
    source = "verb"
    reason = f"Verb '{cmd['verb']}'"
    if level is None:
        level, source, reason = "Modify", "default", "Unknown verb; treated as a configuration change"
    if cmd.get("confirmImpact") == "High":
        raised = _raise(level, RULES["confirmImpactHighRaisesTo"])
        if raised != level:
            level, source, reason = raised, "confirmImpact", "Command declares ConfirmImpact = High"
    return {"level": level, "source": source, "reason": reason}


def category_for(cmd):
    for cid, rx in _CATEGORIES:
        if rx.match(cmd["noun"]):
            return cid
    return RULES["fallbackCategory"]


def type_producers(commands):
    """type short name -> commands whose documented output is that type."""
    out = {}
    for c in commands:
        for t in c.get("outputTypes", []):
            short = t.split(".")[-1].replace("[]", "")
            out.setdefault(short, [])
            if c["name"] not in out[short]:
                out[short].append(c["name"])
    return out


def type_dependencies(cmd, producers):
    deps = []
    for p in cmd["parameters"]:
        for t in object_types_for_parameter(p):
            deps.append({
                "parameter": p["name"],
                "type": t,
                "preferredProducer": _OBJECT_TYPES[t]["producer"],
                "documentedProducers": sorted(producers.get(t, [])),
                "mandatory": p.get("mandatoryInAnySet", False),
            })
    return deps


def gui_suitability(cmd, producers):
    """Returns (level, reasons). Levels: AppManaged, ExpertOnly, CustomWorkflow, AutoForm, NotSuitable."""
    reasons = []
    name = cmd["name"]
    if name in RULES["appManagedCommands"]:
        return "AppManaged", [RULES["appManagedCommands"][name]]
    if cmd.get("internalHelper"):
        return "NotSuitable", ["Internal helper function exported by the module's wildcard export; undocumented"]
    if cmd.get("deprecated"):
        return "NotSuitable", [f"Deprecated; replaced by {cmd.get('replacedBy')}"]
    if name in RULES["expertOnlyCommands"]:
        return "ExpertOnly", [RULES["expertOnlyCommands"][name]]
    if cmd["category"] == "Advanced":
        return "ExpertOnly", ["Low-level SDK / Configuration API command"]
    if cmd["requirements"].get("interactiveSession"):
        return "ExpertOnly", ["Requires an interactive session (shows its own dialog)"]
    if not cmd["parameterSets"] and not cmd["parameters"] and cmd.get("implementation") is None:
        return "NotSuitable", ["No metadata available"]

    # A command can be auto-formed if at least one parameter set has only supported mandatory params.
    usable_sets = []
    for ps in cmd["parameterSets"] or [{"name": "__AllParameterSets", "parameters": [{"name": p["name"], "mandatory": p.get("mandatoryInAnySet")} for p in cmd["parameters"]]}]:
        bad = []
        for sp in ps.get("parameters", []):
            if not sp.get("mandatory"):
                continue
            p = next((x for x in cmd["parameters"] if x["name"] == sp["name"]), None)
            if p is None:
                continue
            if p["control"] in ("Unsupported", "KeyValue"):
                bad.append(f"{p['name']} <{p['type']}>")
            elif p["control"] in ("ObjectSelector", "ObjectMultiSelector") and not object_types_for_parameter(p):
                bad.append(f"{p['name']} <{p['type']}> has no known producer")
        if not bad:
            usable_sets.append(ps["name"])
        else:
            reasons.append(f"Parameter set '{ps['name']}' needs complex input: " + ", ".join(bad))
    risk = cmd["risk"]["level"]
    if risk in ("Delete", "HighImpact", "Sensitive"):
        return "CustomWorkflow", [f"Risk level {risk} requires a guided workflow with review and strong confirmation"] + ([] if usable_sets else reasons)
    if not usable_sets:
        return "CustomWorkflow", reasons
    return "AutoForm", [f"Usable parameter sets: {', '.join(usable_sets)}"]


def apply_classification(catalog):
    """Add control/isSecret, risk, category, GUI suitability and dependencies to every command (in place)."""
    commands = catalog["commands"]
    for c in commands:
        for p in c["parameters"]:
            p.setdefault("mandatoryInAnySet", any(x.get("mandatory") for x in p.get("sets", [])))
            p["control"] = control_for_parameter(p)
            p["isSecret"] = is_secret_parameter(p)
    names_lower = {c["name"].lower(): c["name"] for c in commands}
    for c in commands:
        for a in c.get("aliases", []):
            names_lower.setdefault(a.lower(), c["name"])
    producers = type_producers(commands)
    for c in commands:
        c["risk"] = risk_for(c)
        c["category"] = category_for(c)
        c["guiSuitability"], c["guiSuitabilityReasons"] = gui_suitability(c, producers)
        refs = set()
        for ex in c.get("examples", []):
            for tok in re.findall(r"\b([A-Z][a-z]+-[A-Za-z0-9]+)\b", ex.get("code") or ""):
                n = names_lower.get(tok.lower())
                if n and n != c["name"]:
                    refs.add(n)
        c["dependencies"] = {
            "byParameterType": type_dependencies(c, producers),
            "usedTogetherInExamples": sorted(refs),
        }
    catalog["classificationRulesVersion"] = RULES["rulesVersion"]
    return catalog
